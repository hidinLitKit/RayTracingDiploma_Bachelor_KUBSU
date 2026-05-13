using UnityEngine;

public static class PropertyRegistryIDs
{
	public const string RaytraceRenderPass = "RaytraceRenderPass";
	public const string RaytraceRenderGen = "RaytraceRenderRayGeneration";
	public const string RaytraceRenderEffect = "RaytraceRender";
	public const string AccelerationStructureName = "_RaytracingAccelerationStructure";

	public static readonly int InverseProjectionMatrix = Shader.PropertyToID("_InverseProjectionMatrix");
	public static readonly int ViewProjectionMatrix = Shader.PropertyToID("_ViewProjectionMatrix");
	public static readonly int CameraToWorldMatrix = Shader.PropertyToID("_CameraToWorldMatrix");
	public static readonly int WorldSpaceCameraPos = Shader.PropertyToID("_WorldSpaceCameraPos");

	public static readonly int RaytraceAgainstLayers = Shader.PropertyToID("_RaytraceAgainstLayers");
	public static readonly int RayFlags = Shader.PropertyToID("_RayFlags");
	public static readonly int MaxRayDistance = Shader.PropertyToID("_MaxRayDistance");

	public static readonly int RenderTarget = Shader.PropertyToID("_RenderTarget");
	public static readonly int TemporallyRendered = Shader.PropertyToID("_TemporallyRendered");
	public static readonly int CopyBlitTex = Shader.PropertyToID("_CopyBlitTex");
}

[System.Flags]
public enum RayFlags
{
	RAY_FLAG_NONE = 0x00,
	RAY_FLAG_FORCE_OPAQUE = 0x01,
	RAY_FLAG_FORCE_NON_OPAQUE = 0x02,
	RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH = 0x04,
	RAY_FLAG_SKIP_CLOSEST_HIT_SHADER = 0x08,
	RAY_FLAG_CULL_BACK_FACING_TRIANGLES = 0x10,
	RAY_FLAG_CULL_FRONT_FACING_TRIANGLES = 0x20,
	RAY_FLAG_CULL_OPAQUE = 0x40,
	RAY_FLAG_CULL_NON_OPAQUE = 0x80,
	RAY_FLAG_SKIP_TRIANGLES = 0x100,
	RAY_FLAG_SKIP_PROCEDURAL_PRIMITIVES = 0x200,
}