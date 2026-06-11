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

		public static readonly int LightAngularRadius = Shader.PropertyToID("_LightAngularRadius");
		public static readonly int ProjScaleY = Shader.PropertyToID("_ProjScaleY");
		public static readonly int PcssMaxRadius = Shader.PropertyToID("_PcssMaxRadius");
		public static readonly int DepthRejectScale = Shader.PropertyToID("_DepthRejectScale");

		public static readonly int AnyHitFarCutout = Shader.PropertyToID("_AnyHitFarCutout");
		public static readonly int AnyHitAlphaMip = Shader.PropertyToID("_AnyHitAlphaMip");

		public static readonly int ShadowStrength = Shader.PropertyToID("_ShadowStrength");
		public static readonly int TextureWidth = Shader.PropertyToID("_TextureWidth");
		public static readonly int TextureHeight = Shader.PropertyToID("_TextureHeight");
		public static readonly int ShadowMap = Shader.PropertyToID("_ShadowMap");
		public static readonly int ShadowmapTemp = Shader.PropertyToID("_ShadowmapTemp");
		public static readonly int ScreenSpaceOcclusionTexture = Shader.PropertyToID("_ScreenSpaceOcclusionTexture");
		public static readonly int RaytracingShadowMap = Shader.PropertyToID("_RaytracingShadowMap");
		public static readonly int RaytracingShadowMapTexelSize = Shader.PropertyToID("_RaytracingShadowMap_TexelSize");

		public static readonly int CurrentTex = Shader.PropertyToID("_CurrentTex");
		public static readonly int HistoryTex = Shader.PropertyToID("_HistoryTex");
		public static readonly int AccumTex = Shader.PropertyToID("_AccumTex");
		public static readonly int TemporalAlpha = Shader.PropertyToID("_TemporalAlpha");
		public static readonly int TemporalDepthReject = Shader.PropertyToID("_TemporalDepthReject");

		//public static readonly GlobalKeyword HAS_DEPTH_NORMALS = new GlobalKeyword("_HAS_DEPTH_NORMALS");
		public static readonly GlobalKeyword SCREEN_SPACE_OCCLUSION = new GlobalKeyword("_SCREEN_SPACE_OCCLUSION");

		public static readonly string RaytraceShadowsEffect = "RaytraceShadows";
		public static readonly string RaytraceShadowsPass = "RaytraceShadowsPass";
		public static readonly string ShadowRayGen = "ShadowRayGeneration";
	}
	// Temporal history (persistent ping-pong) provided by the feature each frame.
	private RTHandle m_historyRead;
	private RTHandle m_historyWrite;
	private bool m_temporalReset;

	public RaytraceShadowsPass(RaytraceFeatureSettings settings) : base(settings)
	{

	}

	public void SetTemporalHistory(RTHandle read, RTHandle write, bool reset)
	{
		m_historyRead = read;
		m_historyWrite = write;
		m_temporalReset = reset;
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

		// R = visibility, G = penumbra radius (pixels), B = view depth (bilateral blur).
		desc.colorFormat = RenderTextureFormat.ARGBFloat;
		desc.graphicsFormat = GraphicsFormat.R32G32B32A32_SFloat;
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
			passData.floatParams.z = shadowSettings.shadowStrength;
			passData.floatParams.w = shadowSettings.lightAngularRadius * Mathf.Deg2Rad;
			passData.intParams.x = shadowSettings.shadowCastCount;
			passData.intParams.y = shadowSettings.pcssMaxRadius;
			passData.floatParams1.x = shadowSettings.pcssDepthRejection;
			passData.boolParams.x = shadowSettings.smoothShadows;
			passData.computeShader0 = settings.rayTraceConfig.shadowFilteringCs;
			passData.computeShader1 = settings.rayTraceConfig.temporalResolveCs;
			passData.floatParams2.y = m_temporalReset ? 1.0f : shadowSettings.temporalAlpha;
			passData.floatParams2.z = shadowSettings.temporalDepthReject;
		}

		// Resolved/filtered shadow map: R = shadow factor, G = view depth (for the
		// depth-aware upsample in the Lit pass, so shadows don't leak through foliage).
		var resolveDesc = desc;
		resolveDesc.colorFormat = RenderTextureFormat.RGHalf;
		resolveDesc.graphicsFormat = GraphicsFormat.R16G16_SFloat;

		TextureHandle resolveShadowTarget = UniversalRenderer.CreateRenderGraphTexture(renderGraph, resolveDesc, "_ShadowMap", false);
		passData.extraTarget0 = resolveShadowTarget;
		builder.UseTexture(passData.extraTarget0, AccessFlags.Write);

		builder.SetGlobalTextureAfterPass(resolveShadowTarget, LocalPropertyRegistryIDs.RaytracingShadowMap);

		// Import the persistent temporal history (ping-pong) for accumulation.
		if (m_historyRead != null && m_historyWrite != null)
		{
			passData.extraTarget2 = renderGraph.ImportTexture(m_historyRead);
			passData.extraTarget3 = renderGraph.ImportTexture(m_historyWrite);
			builder.UseTexture(passData.extraTarget2, AccessFlags.Read);
			builder.UseTexture(passData.extraTarget3, AccessFlags.ReadWrite);
		}
	}

	protected override void ConfigureCustomRaytraceCommands(UnsafeCommandBuffer cmd, PassData data)
	{
		base.ConfigureCustomRaytraceCommands(cmd, data);
		cmd.SetGlobalFloat(LocalPropertyRegistryIDs.ShadowBias, data.floatParams.x);
		cmd.SetGlobalInt(LocalPropertyRegistryIDs.ShadowCastCount, data.intParams.x);

		// PCSS: light cone half-angle + world->pixel projection scale (fovY based).
		cmd.SetGlobalFloat(LocalPropertyRegistryIDs.LightAngularRadius, data.floatParams.w);

		float fovYRad = data.camera.fieldOfView * Mathf.Deg2Rad;
		float projScaleY = data.height / (2.0f * Mathf.Tan(fovYRad * 0.5f));
		cmd.SetGlobalFloat(LocalPropertyRegistryIDs.ProjScaleY, projScaleY);

		// Any-hit cost controls (shared with the volumetric pass via the shadow hit group).
		cmd.SetGlobalFloat(LocalPropertyRegistryIDs.AnyHitFarCutout, settings.rayTraceConfig.anyHitFarCutout);
		cmd.SetGlobalFloat(LocalPropertyRegistryIDs.AnyHitAlphaMip, settings.rayTraceConfig.anyHitAlphaMip);

		// Texel size of the resolved shadow map for the depth-aware upsample in the Lit pass.
		cmd.SetGlobalVector(LocalPropertyRegistryIDs.RaytracingShadowMapTexelSize,
			new Vector4(1.0f / data.width, 1.0f / data.height, data.width, data.height));
		//cmd.DisableKeyword(LocalPropertyRegistryIDs.HAS_DEPTH_NORMALS);
	}

	protected override void AppendCommandBufferAfterDispatch(UnsafeCommandBuffer cmd, PassData data)
	{
		base.AppendCommandBufferAfterDispatch(cmd, data);

		TextureHandle shadowSource = data.raytraceTarget;

		// Temporal accumulation on the raw shadow buffer (before the spatial PCSS filter).
		if (data.computeShader1 != null && data.extraTarget2.IsValid() && data.extraTarget3.IsValid())
		{
			const int tkernel = 1; // TemporalResolveShadow (visibility in r, depth in b)
			cmd.SetComputeIntParam(data.computeShader1, LocalPropertyRegistryIDs.TextureWidth, data.width);
			cmd.SetComputeIntParam(data.computeShader1, LocalPropertyRegistryIDs.TextureHeight, data.height);
			cmd.SetComputeFloatParam(data.computeShader1, LocalPropertyRegistryIDs.TemporalAlpha, data.floatParams2.y);
			cmd.SetComputeFloatParam(data.computeShader1, LocalPropertyRegistryIDs.TemporalDepthReject, data.floatParams2.z);
			cmd.SetComputeTextureParam(data.computeShader1, tkernel, LocalPropertyRegistryIDs.CurrentTex, data.raytraceTarget);
			cmd.SetComputeTextureParam(data.computeShader1, tkernel, LocalPropertyRegistryIDs.HistoryTex, data.extraTarget2);
			cmd.SetComputeTextureParam(data.computeShader1, tkernel, LocalPropertyRegistryIDs.AccumTex, data.extraTarget3);

			int tgx = Mathf.CeilToInt(data.width / 8.0f);
			int tgy = Mathf.CeilToInt(data.height / 8.0f);
			cmd.DispatchCompute(data.computeShader1, tkernel, tgx, tgy, 1);

			shadowSource = data.extraTarget3;
		}

		int kernel = data.boolParams.x ? 0 : 1;
		cmd.SetComputeFloatParam(data.computeShader0, LocalPropertyRegistryIDs.ShadowStrength, data.floatParams.z);
		cmd.SetComputeIntParam(data.computeShader0, LocalPropertyRegistryIDs.TextureWidth, data.width);
		cmd.SetComputeIntParam(data.computeShader0, LocalPropertyRegistryIDs.TextureHeight, data.height);
		cmd.SetComputeIntParam(data.computeShader0, LocalPropertyRegistryIDs.PcssMaxRadius, Mathf.Max(1, data.intParams.y));
		cmd.SetComputeFloatParam(data.computeShader0, LocalPropertyRegistryIDs.DepthRejectScale, data.floatParams1.x);
		cmd.SetComputeTextureParam(data.computeShader0, kernel, LocalPropertyRegistryIDs.ShadowmapTemp, shadowSource);
		cmd.SetComputeTextureParam(data.computeShader0, kernel, LocalPropertyRegistryIDs.ShadowMap, data.extraTarget0);

		int groupsX = Mathf.CeilToInt(data.width / 32.0f);
		int groupsY = Mathf.CeilToInt(data.height / 32.0f);
		cmd.DispatchCompute(data.computeShader0, kernel, groupsX, groupsY, 1);
	}
}
