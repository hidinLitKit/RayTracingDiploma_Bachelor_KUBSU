using UnityEngine.Rendering.Universal;

public class RaytracePrepareRendererFeature : RaytraceFeatureBase
{
	private RaytracePreparePass m_preparePass;

	public override void Create()
	{
		m_preparePass = new RaytracePreparePass(renderPassEvent);
	}

	public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
	{
		renderer.EnqueuePass(m_preparePass);
	}
}
