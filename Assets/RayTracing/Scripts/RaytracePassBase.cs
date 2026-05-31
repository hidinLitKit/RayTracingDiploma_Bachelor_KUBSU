using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

public class RaytracePassBase : ScriptableRenderPass
{
	protected readonly RaytraceFeatureSettings settings;

	public RaytracePassBase(RaytraceFeatureSettings settings)
	{
		this.settings = settings;
		this.renderPassEvent = renderPassEvent;
	}

	protected class PassData
	{
		//Common pass data
		public RaytracePassBase pass;

		public RayTracingShader shader;
		public TextureHandle raytraceTarget;
		public TextureHandle cameraColorTarget;
		public RayTracingAccelerationStructure accelerationStructure;

		public Camera camera;

		public int width;
		public int height;
		public int volumeDepth;

		public int temporallyRendered;
		public int rayFlags;
		public float maxRayDistance;

		public string accelerationStructureName;
		public string raytracingShaderPassName;
		public string rayGenShaderName;

		// Free var slots
		public float4 floatParams;
		public float4 floatParams1;
		public int4 intParams;
		public bool4 boolParams;
		public TextureHandle extraTarget0;
		public ComputeShader computeShader0;
	}

	public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameContext)
	{
		if (!CanRecord())
		{
			return;
		}

		UniversalResourceData resourceData = frameContext.Get<UniversalResourceData>();
		UniversalCameraData cameraData = frameContext.Get<UniversalCameraData>();

		using (var builder = renderGraph.AddUnsafePass<PassData>(GetEffectName(), out var passData))
		{
			var desc = GetRTDesc(cameraData);

			TextureHandle raytraceTarget =
				UniversalRenderer.CreateRenderGraphTexture(
					renderGraph,
					desc,
					"_" + GetEffectName() + "Tex",
					false);

			passData.pass = this;

			passData.shader = GetRaytracingShader();
			passData.raytraceTarget = raytraceTarget;
			passData.cameraColorTarget = resourceData.activeColorTexture;

			passData.camera = cameraData.camera;

			passData.width = desc.width;
			passData.height = desc.height;
			passData.volumeDepth = Mathf.Max(1, desc.volumeDepth);

			passData.temporallyRendered = 0;
			passData.rayFlags = (int)settings.rayFlags;
			passData.maxRayDistance = settings.maxRayDistance;

			GetRaytracingAccelerationStructureData(out passData.accelerationStructure, out passData.accelerationStructureName);

			passData.raytracingShaderPassName = GetRaytracingShaderPassName();
			passData.rayGenShaderName = GetRayGenShaderName(RenderingPath.Forward);

			builder.UseTexture(passData.raytraceTarget, AccessFlags.ReadWrite);
			builder.UseTexture(passData.cameraColorTarget, AccessFlags.ReadWrite);
			ConfigureRenderGraphBuilder(builder, passData, renderGraph, frameContext, desc);

			builder.AllowPassCulling(false);

			builder.SetRenderFunc((PassData data, UnsafeGraphContext context) =>
			{
				ExecutePass(data, context);
			});
		}
	}

	protected virtual bool CanRecord()
	{
		if (!RaytraceDataManager.instance.IsReady())
		{
			return false;
		}

		if (settings == null || settings.rayTraceConfig == null)
		{
			return false;
		}

		if (GetRaytracingShader() == null)
		{
			return false;
		}

		return true;
	}

	private void ExecutePass(PassData data, UnsafeGraphContext context)
	{
		var cmd = context.cmd;

		ConfigureBaseRaytraceCommands(cmd, data);

		data.pass.ConfigureCustomRaytraceCommands(cmd, data);

		DispatchRays(cmd, data);

		data.pass.AppendCommandBufferAfterDispatch(cmd, data);
	}

	private void ConfigureBaseRaytraceCommands(UnsafeCommandBuffer cmd, PassData data)
	{
		var nativeCmd = CommandBufferHelpers.GetNativeCommandBuffer(cmd);

		nativeCmd.SetRayTracingAccelerationStructure(data.shader, data.accelerationStructureName, data.accelerationStructure);

		nativeCmd.SetGlobalInt(PropertyRegistryIDs.RayFlags,data.rayFlags);
		nativeCmd.SetGlobalFloat(PropertyRegistryIDs.MaxRayDistance, data.maxRayDistance);
		nativeCmd.SetRayTracingShaderPass(data.shader, data.raytracingShaderPassName);
		nativeCmd.SetGlobalInt(PropertyRegistryIDs.TemporallyRendered, data.temporallyRendered);

		nativeCmd.SetRayTracingTextureParam(data.shader, PropertyRegistryIDs.RenderTarget, data.raytraceTarget);
	}

	private void DispatchRays(UnsafeCommandBuffer cmd, PassData data)
	{
		cmd.DispatchRays(
			data.shader,
			data.rayGenShaderName,
			(uint)data.width,
			(uint)data.height,
			(uint)data.volumeDepth,
			data.camera);
	}

	protected virtual RenderTextureDescriptor GetRTDesc(UniversalCameraData cameraData)
	{
		var desc = cameraData.cameraTargetDescriptor;

		var scale = Mathf.Max(1, settings.raySpawnScale);

		desc.width = Mathf.Max(1, desc.width / scale);
		desc.height = Mathf.Max(1, desc.height / scale);

		desc.enableRandomWrite = true;
		desc.msaaSamples = 1;
		desc.depthBufferBits = 0;

		return desc;
	}

	protected virtual void GetRaytracingAccelerationStructureData(out RayTracingAccelerationStructure structure, out string structureName)
	{
		if (!RaytraceDataManager.instance.TryGetRTAS(out structure))
		{
			Debug.LogWarning("RTAS not available");
		}
		structureName = PropertyRegistryIDs.AccelerationStructureName;
	}

	protected virtual string GetEffectName()
	{
		return "";
	}

	protected virtual string GetRaytracingShaderPassName()
	{
		return "";
	}

	protected virtual string GetRayGenShaderName(RenderingPath renderPath)
	{
		return "";
	}

	protected virtual RayTracingShader GetRaytracingShader()
	{
		return null;
	}

	protected virtual void ConfigureRenderGraphBuilder(IUnsafeRenderGraphBuilder builder, PassData passData, RenderGraph renderGraph, ContextContainer frameContext, RenderTextureDescriptor desc)
	{

	}

	protected virtual void ConfigureCustomRaytraceCommands(UnsafeCommandBuffer cmd, PassData data)
	{

	}

	protected virtual void AppendCommandBufferAfterDispatch(UnsafeCommandBuffer cmd, PassData data)
	{

	}
}