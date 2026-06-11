using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

public class RaytraceVolumetricPass : RaytracePassBase
{
	private static class LocalPropertyRegistryIDs
	{
		public static readonly int StepCount = Shader.PropertyToID("_StepCount");
		public static readonly int Density = Shader.PropertyToID("_Density");
		public static readonly int PhaseG = Shader.PropertyToID("_PhaseG");
		public static readonly int FogIntensity = Shader.PropertyToID("_FogIntensity");
		public static readonly int MaxFogDistance = Shader.PropertyToID("_MaxFogDistance");
		public static readonly int ScatterColor = Shader.PropertyToID("_ScatterColor");
		public static readonly int CameraDepthTexture = Shader.PropertyToID("_CameraDepthTexture");

		public static readonly int RTXVolumetricsTex = Shader.PropertyToID("_RTXVolumetricsTex");
		public static readonly int VolumetricsGrabpass = Shader.PropertyToID("_VolumetricsGrabpass");

		public static readonly int TextureWidth = Shader.PropertyToID("_TextureWidth");
		public static readonly int TextureHeight = Shader.PropertyToID("_TextureHeight");
		public static readonly int BlurRadius = Shader.PropertyToID("_BlurRadius");
		public static readonly int BlurDepthReject = Shader.PropertyToID("_BlurDepthReject");
		public static readonly int VolumetricTemp = Shader.PropertyToID("_VolumetricTemp");
		public static readonly int VolumetricBlurred = Shader.PropertyToID("_VolumetricBlurred");

		public static readonly int AnyHitFarCutout = Shader.PropertyToID("_AnyHitFarCutout");
		public static readonly int AnyHitAlphaMip = Shader.PropertyToID("_AnyHitAlphaMip");

		public static readonly int CurrentTex = Shader.PropertyToID("_CurrentTex");
		public static readonly int HistoryTex = Shader.PropertyToID("_HistoryTex");
		public static readonly int AccumTex = Shader.PropertyToID("_AccumTex");
		public static readonly int TemporalAlpha = Shader.PropertyToID("_TemporalAlpha");
		public static readonly int TemporalDepthReject = Shader.PropertyToID("_TemporalDepthReject");

		public static readonly string RaytraceVolumetricEffect = "RaytraceVolumetric";
		// Reuse the shadow hit group for the per-step occlusion test toward the sun.
		public static readonly string RaytraceShadowsPass = "RaytraceShadowsPass";
		public static readonly string VolumetricRayGen = "VolumetricRayGen";
	}

	// Temporal history
	private RTHandle m_historyRead;
	private RTHandle m_historyWrite;
	private bool m_temporalReset;

	public RaytraceVolumetricPass(RaytraceFeatureSettings settings) : base(settings)
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
		return LocalPropertyRegistryIDs.RaytraceVolumetricEffect;
	}

	protected override string GetRaytracingShaderPassName()
	{
		return LocalPropertyRegistryIDs.RaytraceShadowsPass;
	}

	protected override string GetRayGenShaderName(RenderingPath renderingPath)
	{
		return LocalPropertyRegistryIDs.VolumetricRayGen;
	}

	protected override RayTracingShader GetRaytracingShader()
	{
		return settings.rayTraceConfig.volumetricRayTracingShader;
	}

	protected override RenderTextureDescriptor GetRTDesc(UniversalCameraData cameraData)
	{
		var desc = base.GetRTDesc(cameraData);

		desc.colorFormat = RenderTextureFormat.ARGBHalf;
		desc.graphicsFormat = GraphicsFormat.R16G16B16A16_SFloat;
		desc.enableRandomWrite = true;
		desc.msaaSamples = 1;
		desc.depthBufferBits = 0;
		desc.autoGenerateMips = false;

		return desc;
	}

	protected override void ConfigureRenderGraphBuilder(IUnsafeRenderGraphBuilder builder, PassData passData, RenderGraph renderGraph, ContextContainer frameContext, RenderTextureDescriptor desc)
	{
		base.ConfigureRenderGraphBuilder(builder, passData, renderGraph, frameContext, desc);

		builder.AllowGlobalStateModification(true);

		if (settings is RaytraceVolumetricRendererFeature.RaytraceVolumetricFeatureSettings volumetricSettings)
		{
			passData.intParams.x = volumetricSettings.stepCount;
			passData.floatParams.x = volumetricSettings.density;
			passData.floatParams.y = volumetricSettings.phaseG;
			passData.floatParams.z = volumetricSettings.fogIntensity;
			passData.floatParams.w = volumetricSettings.maxFogDistance;

			var c = volumetricSettings.scatterColor;
			passData.floatParams1 = new float4(c.r, c.g, c.b, c.a);

			passData.intParams.y = volumetricSettings.blurRadius;
			passData.floatParams2.x = volumetricSettings.blurDepthReject;

			passData.material0 = settings.rayTraceConfig.volumetricApplyMaterial;
			passData.computeShader0 = settings.rayTraceConfig.volumetricBlurCs;
			passData.computeShader1 = settings.rayTraceConfig.temporalResolveCs;
			passData.floatParams2.y = m_temporalReset ? 1.0f : volumetricSettings.temporalAlpha;
			passData.floatParams2.z = volumetricSettings.temporalDepthReject;
		}

		// Denoised fog target (bilateral blur output)
		TextureHandle blurred = UniversalRenderer.CreateRenderGraphTexture(renderGraph, desc, "_VolumetricBlurredRT", false);
		passData.extraTarget1 = blurred;
		builder.UseTexture(passData.extraTarget1, AccessFlags.ReadWrite);


		var resourceData = frameContext.Get<UniversalResourceData>();
		passData.depthTexture = resourceData.cameraDepthTexture;
		if (passData.depthTexture.IsValid())
		{
			builder.UseTexture(passData.depthTexture, AccessFlags.Read);
		}

		// Grabpass snapshot of the camera color for the additive composite.
		var cameraData = frameContext.Get<UniversalCameraData>();
		var grabDesc = cameraData.cameraTargetDescriptor;
		grabDesc.depthBufferBits = 0;
		grabDesc.msaaSamples = 1;
		grabDesc.enableRandomWrite = false;

		TextureHandle grabpass = UniversalRenderer.CreateRenderGraphTexture(renderGraph, grabDesc, "_VolumetricsGrabpassRT", false);
		passData.extraTarget0 = grabpass;
		builder.UseTexture(passData.extraTarget0, AccessFlags.ReadWrite);

		// ping-pong history
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

		cmd.SetGlobalInt(LocalPropertyRegistryIDs.StepCount, Mathf.Max(1, data.intParams.x));
		cmd.SetGlobalFloat(LocalPropertyRegistryIDs.Density, data.floatParams.x);
		cmd.SetGlobalFloat(LocalPropertyRegistryIDs.PhaseG, data.floatParams.y);
		cmd.SetGlobalFloat(LocalPropertyRegistryIDs.FogIntensity, data.floatParams.z);
		cmd.SetGlobalFloat(LocalPropertyRegistryIDs.MaxFogDistance, data.floatParams.w);
		cmd.SetGlobalVector(LocalPropertyRegistryIDs.ScatterColor, new Vector4(data.floatParams1.x, data.floatParams1.y, data.floatParams1.z, data.floatParams1.w));

		// Same any-hit cost controls as the shadow pass (shared hit group).
		cmd.SetGlobalFloat(LocalPropertyRegistryIDs.AnyHitFarCutout, settings.rayTraceConfig.anyHitFarCutout);
		cmd.SetGlobalFloat(LocalPropertyRegistryIDs.AnyHitAlphaMip, settings.rayTraceConfig.anyHitAlphaMip);

		if (data.depthTexture.IsValid())
		{
			var nativeCmd = CommandBufferHelpers.GetNativeCommandBuffer(cmd);
			nativeCmd.SetRayTracingTextureParam(data.shader, LocalPropertyRegistryIDs.CameraDepthTexture, data.depthTexture);
		}
	}

	protected override void AppendCommandBufferAfterDispatch(UnsafeCommandBuffer cmd, PassData data)
	{
		base.AppendCommandBufferAfterDispatch(cmd, data);

		if (data.material0 == null)
		{
			return;
		}

		var nativeCmd = CommandBufferHelpers.GetNativeCommandBuffer(cmd);

		TextureHandle fogSource = data.raytraceTarget;

		// Bilateral-blur the noisy low-res fog before composite (denoise).
		if (data.computeShader0 != null && data.extraTarget1.IsValid() && data.intParams.y > 0)
		{
			const int kernel = 0;
			cmd.SetComputeIntParam(data.computeShader0, LocalPropertyRegistryIDs.TextureWidth, data.width);
			cmd.SetComputeIntParam(data.computeShader0, LocalPropertyRegistryIDs.TextureHeight, data.height);
			cmd.SetComputeIntParam(data.computeShader0, LocalPropertyRegistryIDs.BlurRadius, data.intParams.y);
			cmd.SetComputeFloatParam(data.computeShader0, LocalPropertyRegistryIDs.BlurDepthReject, data.floatParams2.x);
			cmd.SetComputeTextureParam(data.computeShader0, kernel, LocalPropertyRegistryIDs.VolumetricTemp, data.raytraceTarget);
			cmd.SetComputeTextureParam(data.computeShader0, kernel, LocalPropertyRegistryIDs.VolumetricBlurred, data.extraTarget1);

			int groupsX = Mathf.CeilToInt(data.width / 8.0f);
			int groupsY = Mathf.CeilToInt(data.height / 8.0f);
			cmd.DispatchCompute(data.computeShader0, kernel, groupsX, groupsY, 1);

			fogSource = data.extraTarget1;
		}

		// Temporal accumulation
		if (data.computeShader1 != null && data.extraTarget2.IsValid() && data.extraTarget3.IsValid())
		{
			const int kernel = 0;
			cmd.SetComputeIntParam(data.computeShader1, LocalPropertyRegistryIDs.TextureWidth, data.width);
			cmd.SetComputeIntParam(data.computeShader1, LocalPropertyRegistryIDs.TextureHeight, data.height);
			cmd.SetComputeFloatParam(data.computeShader1, LocalPropertyRegistryIDs.TemporalAlpha, data.floatParams2.y);
			cmd.SetComputeFloatParam(data.computeShader1, LocalPropertyRegistryIDs.TemporalDepthReject, data.floatParams2.z);
			cmd.SetComputeTextureParam(data.computeShader1, kernel, LocalPropertyRegistryIDs.CurrentTex, fogSource);
			cmd.SetComputeTextureParam(data.computeShader1, kernel, LocalPropertyRegistryIDs.HistoryTex, data.extraTarget2);
			cmd.SetComputeTextureParam(data.computeShader1, kernel, LocalPropertyRegistryIDs.AccumTex, data.extraTarget3);

			int groupsX = Mathf.CeilToInt(data.width / 8.0f);
			int groupsY = Mathf.CeilToInt(data.height / 8.0f);
			cmd.DispatchCompute(data.computeShader1, kernel, groupsX, groupsY, 1);

			fogSource = data.extraTarget3;
		}

		nativeCmd.SetGlobalTexture(LocalPropertyRegistryIDs.RTXVolumetricsTex, fogSource);

		// Bind depth for the apply pass's bilateral upsample.
		if (data.depthTexture.IsValid())
		{
			nativeCmd.SetGlobalTexture(LocalPropertyRegistryIDs.CameraDepthTexture, data.depthTexture);
		}

		// snapshot of camera color.
		nativeCmd.SetGlobalTexture(PropertyRegistryIDs.CopyBlitTex, data.cameraColorTarget);
		nativeCmd.SetRenderTarget(data.extraTarget0);
		nativeCmd.DrawProcedural(Matrix4x4.identity, data.material0, 0, MeshTopology.Triangles, 3);

		// add in-scattered fog back into the camera color.
		nativeCmd.SetGlobalTexture(LocalPropertyRegistryIDs.VolumetricsGrabpass, data.extraTarget0);
		nativeCmd.SetRenderTarget(data.cameraColorTarget);
		nativeCmd.DrawProcedural(Matrix4x4.identity, data.material0, 1, MeshTopology.Triangles, 3);
	}
}
