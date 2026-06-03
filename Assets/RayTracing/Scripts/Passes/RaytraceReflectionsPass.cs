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

		public static readonly int RTXReflectionsTex = Shader.PropertyToID("_RTXReflectionsTex");
		public static readonly int ReflectionsGrabpass = Shader.PropertyToID("_ReflectionsGrabpass");

		public static readonly string RaytraceReflectionsEffect = "RaytraceReflections";
		public static readonly string RaytraceReflectionsPass = "RaytraceReflectionPass";
		public static readonly string ReflectionsRayGen = "ReflectionRayGeneration";
	}

	public RaytraceReflectionsPass(RaytraceFeatureSettings settings) : base(settings)
	{
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
	}

	protected override void ConfigureCustomRaytraceCommands(UnsafeCommandBuffer cmd, PassData data)
	{
		base.ConfigureCustomRaytraceCommands(cmd, data);

		int bounces = Mathf.Max(1, data.intParams.x);
		cmd.SetGlobalFloat(LocalPropertyRegistryIDs.MaxBounces, bounces);
		cmd.SetGlobalFloat(LocalPropertyRegistryIDs.BounceFalloff, 1.0f / (bounces + 1));
		cmd.SetGlobalInt(LocalPropertyRegistryIDs.MaxRoughnessRays, Mathf.Max(1, data.intParams.y));
		cmd.SetGlobalFloat(LocalPropertyRegistryIDs.ShadowBounceCap, data.floatParams.x);

		if (data.texture0 != null)
		{
			var nativeCmd = CommandBufferHelpers.GetNativeCommandBuffer(cmd);
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

		nativeCmd.SetGlobalTexture(LocalPropertyRegistryIDs.RTXReflectionsTex, data.raytraceTarget);

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
