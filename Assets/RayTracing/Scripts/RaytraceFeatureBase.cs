using UnityEngine;
using UnityEngine.Rendering.Universal;

[System.Serializable]
public class RaytraceFeatureSettings
{
	[Header("Global Config")]
	public RaytraceConfig rayTraceConfig;

	[Header("RTX Settings"), Tooltip("RT Shader uses those flags to handle intersections.")]
	public RayFlags rayFlags = RayFlags.RAY_FLAG_CULL_BACK_FACING_TRIANGLES;

	[Header("RTX Quality Settings")]
	[Range(1, 5), Tooltip("Overall screen scale for ray spawning. Higher value means more downscaled RT spawn area compared to screen size.")]
	public int raySpawnScale = 1;
	[Range(1f, 1000f), Tooltip("This is the 'view distance' of rays. Higher values will have worse performance.")]
	public float maxRayDistance = 100f;
}
public class RaytraceFeatureBase : ScriptableRendererFeature
{
	public RaytraceFeatureSettings settings;
	public RenderPassEvent renderPassEvent = RenderPassEvent.AfterRenderingSkybox;
	private RaytracePassBase m_renderPass;

	public override void Create()
	{
		m_renderPass = new RaytracePassBase(settings);
	}

	public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
	{
		renderer.EnqueuePass(m_renderPass);
	}
}
