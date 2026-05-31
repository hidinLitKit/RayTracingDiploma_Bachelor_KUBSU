using System;
using UnityEngine;
using UnityEngine.Rendering.Universal;

public class RaytraceShadowsRendererFeature : RaytraceFeatureBase
{
	[Serializable]
	public class RaytraceShadowsFeatureSettings : RaytraceFeatureSettings
	{
		[Header("Shadow settings")]
		[Range(0f, 0.5f)]   public float shadowBias = 0.001f;
		[Range(0f, 1f)]         public float shadowStrength = 1f;
		[Range(0.0001f, 0.01f)] public float shadowRaySeparation = 0.001f;
		[Range(1, 32)]          public int shadowCastCount = 16;
		public bool smoothShadows = false;

		[Header("PCSS (contact-hardening soft shadows)")]
		[Range(0.05f, 10f)]     public float lightAngularRadius = 1.0f;
		[Range(1, 32)]          public int pcssMaxRadius = 16;
		[Range(0.005f, 0.5f)]   public float pcssDepthRejection = 0.05f;
	}

	public RaytraceShadowsFeatureSettings raytraceShadowsSettings = new();
	private RaytraceShadowsPass m_shadowsPass;

	public override void Create()
	{
		m_shadowsPass = new RaytraceShadowsPass(raytraceShadowsSettings)
		{
			renderPassEvent = RenderPassEvent.BeforeRenderingOpaques
		};
	}

	public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
	{
		renderer.EnqueuePass(m_shadowsPass);
	}
}
