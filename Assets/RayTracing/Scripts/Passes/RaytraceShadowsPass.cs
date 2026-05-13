using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

public class RaytraceShadowsPass : RaytracePassBase
{
	private static class LocalPropertyRegistryIDs
	{
		public static readonly int ShadowBias = Shader.PropertyToID("_RtShadowBias");
		public static readonly int ShadowCastCount = Shader.PropertyToID("_ShadowCastCount");
		public static readonly int RaySeparation = Shader.PropertyToID("_RaySeparation");

		public static readonly int ShadowStrength = Shader.PropertyToID("_ShadowStrength");
		public static readonly int TextureWidth = Shader.PropertyToID("_TextureWidth");
		public static readonly int TextureHeight = Shader.PropertyToID("_TextureHeight");
		public static readonly int ShadowMap = Shader.PropertyToID("_ShadowMap");
		public static readonly int ShadowmapTemp = Shader.PropertyToID("_ShadowmapTemp");
		public static readonly int ScreenSpaceOcclusionTexture = Shader.PropertyToID("_ScreenSpaceOcclusionTexture");
		public static readonly int RaytracingShadowMap = Shader.PropertyToID("_RaytracingShadowMap");

		//public static readonly GlobalKeyword HAS_DEPTH_NORMALS = new GlobalKeyword("_HAS_DEPTH_NORMALS");
		public static readonly GlobalKeyword SCREEN_SPACE_OCCLUSION = new GlobalKeyword("_SCREEN_SPACE_OCCLUSION");

		public static readonly string RaytraceShadowsEffect = "RaytraceShadows";
		public static readonly string RaytraceShadowsPass = "RaytraceShadowsPass";
		public static readonly string ShadowRayGen = "ShadowRayGeneration";
	}
	public RaytraceShadowsPass(RaytraceFeatureSettings settings) : base(settings)
	{

	}

	protected override string GetEffectName()
	{
		return LocalPropertyRegistryIDs.RaytraceShadowsEffect;
	}

	protected override string GetRaytracingShaderPassName()
	{
		return LocalPropertyRegistryIDs.RaytraceShadowsPass;
	}

	protected override string GetRayGenShaderName(RenderingPath renderingPath)
	{
		return LocalPropertyRegistryIDs.ShadowRayGen;
	}

	protected override RenderTextureDescriptor GetRTDesc(UniversalCameraData cameraData)
	{
		var desc = base.GetRTDesc(cameraData);

		desc.colorFormat = RenderTextureFormat.RFloat;
		desc.graphicsFormat = GraphicsFormat.R32_SFloat;
		desc.enableRandomWrite = true;
		desc.msaaSamples = 1;
		desc.depthBufferBits = 0;

		return desc;
	}

	protected override RayTracingShader GetRaytracingShader()
	{
		return settings.rayTraceConfig.shadowRayTracingShader;
	}

	protected override void ConfigureRenderGraphBuilder(IUnsafeRenderGraphBuilder builder, PassData passData, RenderGraph renderGraph, ContextContainer frameContext, RenderTextureDescriptor desc)
	{
		base.ConfigureRenderGraphBuilder(builder, passData, renderGraph, frameContext, desc);
		if (settings is RaytraceShadowsRendererFeature.RaytraceShadowsFeatureSettings shadowSettings)
		{
			passData.floatParams.x = shadowSettings.shadowBias;
			passData.floatParams.y = shadowSettings.shadowRaySeparation;
			passData.floatParams.z = shadowSettings.shadowStrength;
			passData.intParams.x = shadowSettings.shadowCastCount;
			passData.boolParams.x = shadowSettings.smoothShadows;
			passData.computeShader0 = settings.rayTraceConfig.shadowFilteringCs;
		}

		TextureHandle resolveShadowTarget = UniversalRenderer.CreateRenderGraphTexture(renderGraph, desc, "_ShadowMap", false);
		passData.extraTarget0 = resolveShadowTarget;
		builder.UseTexture(passData.extraTarget0, AccessFlags.Write);
		builder.SetGlobalTextureAfterPass(resolveShadowTarget, LocalPropertyRegistryIDs.RaytracingShadowMap);
		builder.SetGlobalTextureAfterPass(resolveShadowTarget, LocalPropertyRegistryIDs.ScreenSpaceOcclusionTexture);
	}

	protected override void ConfigureCustomRaytraceCommands(UnsafeCommandBuffer cmd, PassData data)
	{
		base.ConfigureCustomRaytraceCommands(cmd, data);
		cmd.SetRayTracingFloatParam(data.shader, LocalPropertyRegistryIDs.ShadowBias, data.floatParams.x);
		cmd.SetRayTracingFloatParam(data.shader, LocalPropertyRegistryIDs.RaySeparation, data.floatParams.y);
		cmd.SetRayTracingIntParam(data.shader, LocalPropertyRegistryIDs.ShadowCastCount, data.intParams.x);
		//cmd.DisableKeyword(LocalPropertyRegistryIDs.HAS_DEPTH_NORMALS);
	}

	protected override void AppendCommandBufferAfterDispatch(UnsafeCommandBuffer cmd, PassData data)
	{
		base.AppendCommandBufferAfterDispatch(cmd, data);
		int kernel = data.boolParams.x ? 0 : 1;
		cmd.SetComputeFloatParam(data.computeShader0, LocalPropertyRegistryIDs.ShadowStrength, data.floatParams.z);
		cmd.SetComputeIntParam(data.computeShader0, LocalPropertyRegistryIDs.TextureWidth, data.width);
		cmd.SetComputeIntParam(data.computeShader0, LocalPropertyRegistryIDs.TextureHeight, data.height);
		cmd.SetComputeTextureParam(data.computeShader0, kernel, LocalPropertyRegistryIDs.ShadowmapTemp, data.raytraceTarget);
		cmd.SetComputeTextureParam(data.computeShader0, kernel, LocalPropertyRegistryIDs.ShadowMap, data.extraTarget0);

		int groupsX = Mathf.CeilToInt(data.width / 32.0f);
		int groupsY = Mathf.CeilToInt(data.height / 32.0f);
		cmd.DispatchCompute(data.computeShader0, kernel, groupsX, groupsY, 1);

		cmd.EnableKeyword(LocalPropertyRegistryIDs.SCREEN_SPACE_OCCLUSION);
	}
}
