using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

public class RaytraceReflectionsPass : RaytracePassBase
{
	private static class LocalPropertyRegistryIDs
	{
		public static readonly int MaxBounces = Shader.PropertyToID("_MaxReflectionBounces");
		public static readonly int BounceFalloff = Shader.PropertyToID("_BounceFalloff");
		public static readonly int MaxRoughnessRays = Shader.PropertyToID("_MaxRoughnessRays");
		public static readonly int ShadowBounceCap = Shader.PropertyToID("_ShadowBounceCap");
		public static readonly int SkyboxTex = Shader.PropertyToID("SkyboxTex");

		public static readonly int AnyHitFarCutout = Shader.PropertyToID("_AnyHitFarCutout");
		public static readonly int AnyHitAlphaMip = Shader.PropertyToID("_AnyHitAlphaMip");

		public static readonly int RTXReflectionsTex = Shader.PropertyToID("_RTXReflectionsTex");
		public static readonly int ReflectionsGrabpass = Shader.PropertyToID("_ReflectionsGrabpass");
		public static readonly int CameraDepthTexture = Shader.PropertyToID("_CameraDepthTexture");
		public static readonly int RTXReflectionsTexTexelSize = Shader.PropertyToID("_RTXReflectionsTex_TexelSize");

		public static readonly int ReflectionTemp = Shader.PropertyToID("_ReflectionTemp");
		public static readonly int ReflectionBlurred = Shader.PropertyToID("_ReflectionBlurred");
		public static readonly int TextureWidth = Shader.PropertyToID("_TextureWidth");
		public static readonly int TextureHeight = Shader.PropertyToID("_TextureHeight");
		public static readonly int ReflBlurRadius = Shader.PropertyToID("_ReflBlurRadius");
		public static readonly int ReflBlurDepthReject = Shader.PropertyToID("_ReflBlurDepthReject");

		// Temporal reprojection.
		public static readonly int ReflReproj = Shader.PropertyToID("_ReflectionReproj"); // raygen UAV
		public static readonly int ReflReprojTex = Shader.PropertyToID("_ReflReprojTex");  // resolve input
		public static readonly int CurrentTex = Shader.PropertyToID("_CurrentTex");
		public static readonly int HistoryTex = Shader.PropertyToID("_HistoryTex");
		public static readonly int AccumTex = Shader.PropertyToID("_AccumTex");
		public static readonly int ResolvedTex = Shader.PropertyToID("_ResolvedTex");
		public static readonly int TemporalAlpha = Shader.PropertyToID("_TemporalAlpha");
		public static readonly int TemporalDepthReject = Shader.PropertyToID("_TemporalDepthReject");

		public static readonly string RaytraceReflectionsEffect = "RaytraceReflections";
		public static readonly string RaytraceReflectionsPass = "RaytraceReflectionPass";
		public static readonly string ReflectionsRayGen = "ReflectionRayGeneration";
	}

	// Temporal history (persistent ping-pong) provided by the feature each frame.
	private RTHandle m_historyRead;
	private RTHandle m_historyWrite;
	private bool m_temporalReset;

	public RaytraceReflectionsPass(RaytraceFeatureSettings settings) : base(settings)
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
		return LocalPropertyRegistryIDs.RaytraceReflectionsEffect;
	}

	protected override string GetRaytracingShaderPassName()
	{
		return LocalPropertyRegistryIDs.RaytraceReflectionsPass;
	}

	protected override string GetRayGenShaderName(RenderingPath renderingPath)
	{
		return LocalPropertyRegistryIDs.ReflectionsRayGen;
	}

	protected override RenderTextureDescriptor GetRTDesc(UniversalCameraData cameraData)
	{
		var desc = base.GetRTDesc(cameraData);

		// RGB = reflected radiance, A = reflection strength.
		desc.colorFormat = RenderTextureFormat.ARGBHalf;
		desc.graphicsFormat = GraphicsFormat.R16G16B16A16_SFloat;
		desc.enableRandomWrite = true;
		desc.msaaSamples = 1;
		desc.depthBufferBits = 0;
		desc.autoGenerateMips = false;

		return desc;
	}

	protected override RayTracingShader GetRaytracingShader()
	{
		return settings.rayTraceConfig.reflectionRayTracingShader;
	}

	protected override void ConfigureRenderGraphBuilder(IUnsafeRenderGraphBuilder builder, PassData passData, RenderGraph renderGraph, ContextContainer frameContext, RenderTextureDescriptor desc)
	{
		base.ConfigureRenderGraphBuilder(builder, passData, renderGraph, frameContext, desc);

		// We set global textures + draw fullscreen in the render func.
		builder.AllowGlobalStateModification(true);

		if (settings is RaytraceReflectionsRendererFeature.RaytraceReflectionsFeatureSettings reflectionSettings)
		{
			passData.intParams.x = reflectionSettings.maxBounces;
			passData.intParams.y = reflectionSettings.maxRoughnessRays;
			passData.floatParams.x = reflectionSettings.shadowBounceCap;

			passData.material0 = settings.rayTraceConfig.reflectionApplyMaterial;
			passData.texture0 = reflectionSettings.skybox != null
				? reflectionSettings.skybox
				: settings.rayTraceConfig.fallbackSkybox;

			passData.intParams.z = reflectionSettings.blurRadius;
			passData.floatParams.y = reflectionSettings.blurDepthReject;
			passData.computeShader0 = settings.rayTraceConfig.reflectionBlurCs;

			// Temporal: on reset (fresh/realloc'd history) use the current frame only.
			passData.computeShader1 = settings.rayTraceConfig.temporalResolveCs;
			passData.floatParams.z = m_temporalReset ? 1.0f : reflectionSettings.temporalAlpha;
			passData.floatParams.w = reflectionSettings.temporalReject;
		}

		// Blurred reflection target (spatial denoise), same descriptor as the raw buffer.
		TextureHandle reflectionBlurred = UniversalRenderer.CreateRenderGraphTexture(renderGraph, desc, "_ReflectionBlurredRT", false);
		passData.extraTarget1 = reflectionBlurred;
		builder.UseTexture(passData.extraTarget1, AccessFlags.ReadWrite);

		// Reprojection aux (raygen UAV: rg = prevUV, b = path length, a = validity).
		TextureHandle reproj = UniversalRenderer.CreateRenderGraphTexture(renderGraph, desc, "_ReflectionReprojRT", false);
		passData.extraTarget4 = reproj;
		builder.UseTexture(passData.extraTarget4, AccessFlags.ReadWrite);

		// Temporally resolved composite source (rgb = accum color, a = strength).
		TextureHandle resolved = UniversalRenderer.CreateRenderGraphTexture(renderGraph, desc, "_ReflectionResolvedRT", false);
		passData.extraTarget5 = resolved;
		builder.UseTexture(passData.extraTarget5, AccessFlags.ReadWrite);

		// Import the persistent temporal history (ping-pong) when temporal is enabled.
		if (m_historyRead != null && m_historyWrite != null)
		{
			passData.extraTarget2 = renderGraph.ImportTexture(m_historyRead);
			passData.extraTarget3 = renderGraph.ImportTexture(m_historyWrite);
			builder.UseTexture(passData.extraTarget2, AccessFlags.Read);
			builder.UseTexture(passData.extraTarget3, AccessFlags.ReadWrite);
		}
		else
		{
			passData.extraTarget2 = TextureHandle.nullHandle;
			passData.extraTarget3 = TextureHandle.nullHandle;
		}

		// Grabpass target: a full-resolution snapshot of the camera color so the apply
		// pass can read the frame while writing back into it.
		var cameraData = frameContext.Get<UniversalCameraData>();
		var grabDesc = cameraData.cameraTargetDescriptor;
		grabDesc.depthBufferBits = 0;
		grabDesc.msaaSamples = 1;
		grabDesc.enableRandomWrite = false;

		TextureHandle grabpass = UniversalRenderer.CreateRenderGraphTexture(renderGraph, grabDesc, "_ReflectionsGrabpassRT", false);
		passData.extraTarget0 = grabpass;
		builder.UseTexture(passData.extraTarget0, AccessFlags.ReadWrite);

		// Scene depth guides the depth-aware reflection upsample.
		var resourceData = frameContext.Get<UniversalResourceData>();
		passData.depthTexture = resourceData.cameraDepthTexture;
		if (passData.depthTexture.IsValid())
		{
			builder.UseTexture(passData.depthTexture, AccessFlags.Read);
		}
	}

	protected override void ConfigureCustomRaytraceCommands(UnsafeCommandBuffer cmd, PassData data)
	{
		base.ConfigureCustomRaytraceCommands(cmd, data);

		int bounces = Mathf.Max(1, data.intParams.x);
		cmd.SetGlobalFloat(LocalPropertyRegistryIDs.MaxBounces, bounces);
		cmd.SetGlobalFloat(LocalPropertyRegistryIDs.BounceFalloff, 1.0f / (bounces + 1));
		cmd.SetGlobalInt(LocalPropertyRegistryIDs.MaxRoughnessRays, Mathf.Max(1, data.intParams.y));
		cmd.SetGlobalFloat(LocalPropertyRegistryIDs.ShadowBounceCap, data.floatParams.x);

		// Alpha-test cost controls for reflection rays through foliage (shared config values).
		cmd.SetGlobalFloat(LocalPropertyRegistryIDs.AnyHitFarCutout, settings.rayTraceConfig.anyHitFarCutout);
		cmd.SetGlobalFloat(LocalPropertyRegistryIDs.AnyHitAlphaMip, settings.rayTraceConfig.anyHitAlphaMip);

		var nativeCmd = CommandBufferHelpers.GetNativeCommandBuffer(cmd);

		// Bind the reprojection aux UAV the raygen writes (always present; harmless if unused).
		if (data.extraTarget4.IsValid())
		{
			nativeCmd.SetRayTracingTextureParam(data.shader, LocalPropertyRegistryIDs.ReflReproj, data.extraTarget4);
		}

		if (data.texture0 != null)
		{
			nativeCmd.SetRayTracingTextureParam(data.shader, LocalPropertyRegistryIDs.SkyboxTex, data.texture0);
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

		TextureHandle reflectionSource = data.raytraceTarget;

		// Spatial (bilateral) blur on the low-res reflection to kill motion shimmer.
		if (data.computeShader0 != null && data.extraTarget1.IsValid() && data.intParams.z > 0)
		{
			const int kernel = 0;
			cmd.SetComputeIntParam(data.computeShader0, LocalPropertyRegistryIDs.TextureWidth, data.width);
			cmd.SetComputeIntParam(data.computeShader0, LocalPropertyRegistryIDs.TextureHeight, data.height);
			cmd.SetComputeIntParam(data.computeShader0, LocalPropertyRegistryIDs.ReflBlurRadius, data.intParams.z);
			cmd.SetComputeFloatParam(data.computeShader0, LocalPropertyRegistryIDs.ReflBlurDepthReject, data.floatParams.y);
			cmd.SetComputeTextureParam(data.computeShader0, kernel, LocalPropertyRegistryIDs.ReflectionTemp, data.raytraceTarget);
			cmd.SetComputeTextureParam(data.computeShader0, kernel, LocalPropertyRegistryIDs.ReflectionBlurred, data.extraTarget1);
			if (data.depthTexture.IsValid())
			{
				cmd.SetComputeTextureParam(data.computeShader0, kernel, LocalPropertyRegistryIDs.CameraDepthTexture, data.depthTexture);
			}

			int groupsX = Mathf.CeilToInt(data.width / 8.0f);
			int groupsY = Mathf.CeilToInt(data.height / 8.0f);
			cmd.DispatchCompute(data.computeShader0, kernel, groupsX, groupsY, 1);

			reflectionSource = data.extraTarget1;
		}

		// Temporal accumulation of the reflection (reprojection precomputed by the raygen).
		if (data.computeShader1 != null && data.extraTarget2.IsValid() && data.extraTarget3.IsValid()
			&& data.extraTarget4.IsValid() && data.extraTarget5.IsValid())
		{
			const int kernel = 2; // TemporalResolveReflection
			cmd.SetComputeIntParam(data.computeShader1, LocalPropertyRegistryIDs.TextureWidth, data.width);
			cmd.SetComputeIntParam(data.computeShader1, LocalPropertyRegistryIDs.TextureHeight, data.height);
			cmd.SetComputeFloatParam(data.computeShader1, LocalPropertyRegistryIDs.TemporalAlpha, data.floatParams.z);
			cmd.SetComputeFloatParam(data.computeShader1, LocalPropertyRegistryIDs.TemporalDepthReject, data.floatParams.w);
			cmd.SetComputeTextureParam(data.computeShader1, kernel, LocalPropertyRegistryIDs.CurrentTex, reflectionSource);
			cmd.SetComputeTextureParam(data.computeShader1, kernel, LocalPropertyRegistryIDs.ReflReprojTex, data.extraTarget4);
			cmd.SetComputeTextureParam(data.computeShader1, kernel, LocalPropertyRegistryIDs.HistoryTex, data.extraTarget2);
			cmd.SetComputeTextureParam(data.computeShader1, kernel, LocalPropertyRegistryIDs.AccumTex, data.extraTarget3);
			cmd.SetComputeTextureParam(data.computeShader1, kernel, LocalPropertyRegistryIDs.ResolvedTex, data.extraTarget5);

			int groupsX = Mathf.CeilToInt(data.width / 8.0f);
			int groupsY = Mathf.CeilToInt(data.height / 8.0f);
			cmd.DispatchCompute(data.computeShader1, kernel, groupsX, groupsY, 1);

			reflectionSource = data.extraTarget5;
		}

		nativeCmd.SetGlobalTexture(LocalPropertyRegistryIDs.RTXReflectionsTex, reflectionSource);
		nativeCmd.SetGlobalVector(LocalPropertyRegistryIDs.RTXReflectionsTexTexelSize,
			new Vector4(1.0f / data.width, 1.0f / data.height, data.width, data.height));

		// Depth guide for the apply pass's depth-aware upsample.
		if (data.depthTexture.IsValid())
		{
			nativeCmd.SetGlobalTexture(LocalPropertyRegistryIDs.CameraDepthTexture, data.depthTexture);
		}

		// 1) Snapshot the camera color into the grabpass target.
		nativeCmd.SetGlobalTexture(PropertyRegistryIDs.CopyBlitTex, data.cameraColorTarget);
		nativeCmd.SetRenderTarget(data.extraTarget0);
		nativeCmd.DrawProcedural(Matrix4x4.identity, data.material0, 0, MeshTopology.Triangles, 3);

		// 2) Composite reflections over the snapshot, back into the camera color.
		nativeCmd.SetGlobalTexture(LocalPropertyRegistryIDs.ReflectionsGrabpass, data.extraTarget0);
		nativeCmd.SetRenderTarget(data.cameraColorTarget);
		nativeCmd.DrawProcedural(Matrix4x4.identity, data.material0, 1, MeshTopology.Triangles, 3);
	}
}
