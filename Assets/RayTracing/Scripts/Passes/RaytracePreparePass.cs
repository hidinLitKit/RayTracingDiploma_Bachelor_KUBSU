using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

public class RaytracePreparePass : ScriptableRenderPass
{
	private class PassData
	{
		public Matrix4x4 inverseProjection;
		public Matrix4x4 viewProjection;
		public Matrix4x4 cameraToWorld;
		public Vector3 worldSpaceCameraPos;

		public Matrix4x4[] inverseProjectionArray;
		public Matrix4x4[] viewProjectionArray;

		public int raytraceAgainstLayers;
		public float time;
		public float frameIndex;
		public Matrix4x4 prevViewProjection;

	}

	// Held across frames for temporal reprojection.
	private Matrix4x4 m_prevViewProjection = Matrix4x4.identity;

	public RaytracePreparePass(RenderPassEvent passEvent)
	{
		renderPassEvent = passEvent;
	}

	public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameContext)
	{
		if (!RaytraceDataManager.instance.IsReady())
		{
			return;
		}

		UniversalCameraData cameraData = frameContext.Get<UniversalCameraData>();

		using (var builder = renderGraph.AddUnsafePass<PassData>("RayTracePreparePass", out var passData))
		{
			FillPassData(passData, cameraData);

			// Previous-frame VP for temporal reprojection (this frame uses last frame's).
			passData.prevViewProjection = m_prevViewProjection;
			m_prevViewProjection = passData.viewProjection;

			builder.AllowPassCulling(false);
			builder.AllowGlobalStateModification(true);

			builder.SetRenderFunc(static (PassData data, UnsafeGraphContext context) =>
			{
				ExecutePass(data, context);
			});
		}
	}

	private static void FillPassData( PassData data, UniversalCameraData cameraData)
	{
		var camera = cameraData.camera;

		var projection = GL.GetGPUProjectionMatrix(cameraData.GetProjectionMatrix(), false);

		var inverseProjection = projection.inverse;
		var view = cameraData.GetViewMatrix();
		var viewProjection = projection * view;

		data.inverseProjection = inverseProjection;
		data.viewProjection = viewProjection;
		data.cameraToWorld = camera.cameraToWorldMatrix;
		data.worldSpaceCameraPos = camera.transform.position;

		data.raytraceAgainstLayers = RaytraceDataManager.instance.UpdateLayers.value;
		data.time = Time.timeSinceLevelLoad; // matches URP _Time.y used by the forward water
		data.frameIndex = Time.frameCount % 1024;

	}

	private static void ExecutePass(PassData data, UnsafeGraphContext context)
	{
		var cmd = context.cmd;

		cmd.SetGlobalMatrix(PropertyRegistryIDs.InverseProjectionMatrix, data.inverseProjection);
		cmd.SetGlobalMatrix(PropertyRegistryIDs.ViewProjectionMatrix, data.viewProjection);
		cmd.SetGlobalMatrix(PropertyRegistryIDs.CameraToWorldMatrix, data.cameraToWorld);
		cmd.SetGlobalVector(PropertyRegistryIDs.WorldSpaceCameraPos, data.worldSpaceCameraPos);
		cmd.SetGlobalInt(PropertyRegistryIDs.RaytraceAgainstLayers, data.raytraceAgainstLayers);
		cmd.SetGlobalFloat(PropertyRegistryIDs.RtTime, data.time);
		cmd.SetGlobalFloat(PropertyRegistryIDs.FrameIndex, data.frameIndex);
		cmd.SetGlobalMatrix(PropertyRegistryIDs.PrevViewProjectionMatrix, data.prevViewProjection);
	}
}