#include "ShadowsPayload.hlsl"

#pragma shader_feature_local _NORMALMAP
#pragma shader_feature_local_fragment _ALPHATEST_ON
#pragma shader_feature_local_fragment _ALPHAPREMULTIPLY_ON
#pragma shader_feature_local_fragment _EMISSION
#pragma shader_feature_local_fragment _METALLICSPECGLOSSMAP
#pragma shader_feature_local_fragment _SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A
#pragma shader_feature_local_fragment _OCCLUSIONMAP
#pragma shader_feature_local _PARALLAXMAP
#pragma shader_feature_local _ _DETAIL_MULX2 _DETAIL_SCALED
#pragma shader_feature_local_fragment _SPECULARHIGHLIGHTS_OFF
#pragma shader_feature_local_fragment _ENVIRONMENTREFLECTIONS_OFF
#pragma shader_feature_local_fragment _SPECULAR_SETUP
#pragma shader_feature_local _RECEIVE_SHADOWS_OFF
#pragma multi_compile _ _MAIN_LIGHT_SHADOWS
#pragma multi_compile _ _MAIN_LIGHT_SHADOWS_CASCADE
#pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
#pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
#pragma multi_compile_fragment _ _SHADOWS_SOFT
#pragma multi_compile _ LIGHTMAP_SHADOW_MIXING
#pragma multi_compile _ SHADOWS_SHADOWMASK
#pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
#pragma multi_compile _ DIRLIGHTMAP_COMBINED
#pragma multi_compile _ LIGHTMAP_ON
#pragma multi_compile_fog

Texture2D _BaseMap;
SamplerState sampler_BaseMap;
float4 _BaseMap_ST;

float4 _BaseColor;
float _Cutoff;
float _AlphaClip;

float GetRaytracedSurfaceAlpha(AttributeData attributes)
{
	Vertex v0, v1, v2;
	GetVertexData(v0, v1, v2);

	float2 barycentrics = attributes.barycentrics;
	float3 barycentricCoords = float3(1.0 - barycentrics.x - barycentrics.y, barycentrics.x, barycentrics.y);
	float2 texcoord = INTERPOLATE_RAYTRACING_ATTRIBUTE(v0.texcoord, v1.texcoord, v2.texcoord, barycentricCoords);

	float2 uvMainTex = TRANSFORM_TEX(texcoord, _BaseMap);
	return _BaseMap.SampleLevel(sampler_BaseMap, uvMainTex, 0).a * _BaseColor.a;
}

[shader("anyhit")]
void AnyHit_Shadows(inout ShadowsPayload payload : SV_RayPayload, AttributeData attributes : SV_IntersectionAttributes)
{
	if (_AlphaClip > 0.5)
	{
		float alpha = GetRaytracedSurfaceAlpha(attributes);
		if (alpha < _Cutoff)
		{
			IgnoreHit();
		}
	}
}

[shader("closesthit")]
void ClosestHit_Shadows(inout ShadowsPayload payload : SV_RayPayload, AttributeData attributes : SV_IntersectionAttributes)
{
	Vertex v0, v1, v2;
	GetVertexData(v0, v1, v2);

	float3 p0 = v0.position;
	float3 p1 = v1.position;
	float3 p2 = v2.position;

	float3 p0WS = TransformObjectToWorld(p0);
	float3 p1WS = TransformObjectToWorld(p1);
	float3 p2WS = TransformObjectToWorld(p2);

	float3 geometricNormalWS = normalize(cross(p1WS - p0WS, p2WS - p0WS));

	float2 barycentrics = attributes.barycentrics;
	float3 barycentricCoords = float3(1.0 - barycentrics.x - barycentrics.y, barycentrics.x, barycentrics.y);
	float2 texcoord = INTERPOLATE_RAYTRACING_ATTRIBUTE(v0.texcoord, v1.texcoord, v2.texcoord, barycentricCoords);

	float2 uvMainTex = TRANSFORM_TEX(texcoord, _BaseMap);
	float4 color = _BaseMap.SampleLevel(sampler_BaseMap, uvMainTex, 0) * _BaseColor;

	float3 worldPosition = WorldRayOrigin() + WorldRayDirection() * RayTCurrent();

	if (payload.rayType == 1.0)
	{
		float alpha = 1.0;
		if (_AlphaClip > 0.5)
		{
			alpha = step(_Cutoff, color.a);
		}

		// Opacity-weighted: transparent fragments let part of the light through ...
		payload.light += payload.rayIntensity * (1.0 - alpha);

		// ... and contribute to the blocker search proportionally to how opaque they are.
		// RayTCurrent() is exactly the receiver->blocker distance (rays start at the receiver).
		payload.blockerDistSum += RayTCurrent() * alpha;
		payload.blockerCount   += alpha;
		return;
	}


	float3 rayDirection = normalize(_MainLightPosition.xyz);
	geometricNormalWS = normalize(geometricNormalWS);

	// Camera->receiver distance: drives the world->pixel penumbra scale and the
	// bilateral (depth-aware) blur in the filtering compute pass.
	float receiverDist = distance(worldPosition, _WorldSpaceCameraPos);
	payload.viewDepth = receiverDist;

	// Orient the geometric normal toward the camera so the bias offset pushes the
	// shadow-ray origin off the visible surface, regardless of triangle winding.
	if (dot(geometricNormalWS, WorldRayDirection()) > 0.0)
	{
		geometricNormalWS = -geometricNormalWS;
	}

	float3 rayPosition = worldPosition + geometricNormalWS * _RtShadowBias;

	Cast(rayPosition, rayDirection, payload);

	// PCSS penumbra estimation at the receiver.
	// Directional light: penumbra width grows with the receiver->blocker distance.
	if (payload.blockerCount > 0.0)
	{
		float avgBlockerDist = payload.blockerDistSum / payload.blockerCount;
		float penumbraWorld   = avgBlockerDist * tan(_LightAngularRadius);

		payload.penumbra = penumbraWorld * _ProjScaleY / max(1e-3, receiverDist);
	}
	else
	{
		payload.penumbra = 0.0; // fully lit -> no penumbra
	}
}