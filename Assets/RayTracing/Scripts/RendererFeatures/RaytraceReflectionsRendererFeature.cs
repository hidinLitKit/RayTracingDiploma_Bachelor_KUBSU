using System;
using UnityEngine;
using UnityEngine.Rendering.Universal;

public class RaytraceReflectionsRendererFeature : RaytraceFeatureBase
{
	[Serializable]
	public class RaytraceReflectionsFeatureSettings : RaytraceFeatureSettings
	{
		[Header("Reflection settings")]
		[Tooltip("Cubemap used when a reflection ray escapes the scene. Falls back to the config's skybox if empty.")]
		public Texture skybox;

		[Header("Reflection quality")]
		[Range(1, 5), Tooltip("Recursion depth of reflections (reflection in reflection). Each bounce ~doubles cost.")]
		public int maxBounces = 2;
		[Range(1, 32), Tooltip("Max rays in the glossy cone for rough surfaces. Higher = smoother rough reflections, more cost.")]
		public int maxRoughnessRays = 8;
		[Range(0, 8), Tooltip("Stop casting shadow probes inside reflections past this bounce depth.")]
		public float shadowBounceCap = 2;
	}

	public RaytraceReflectionsFeatureSettings raytraceReflectionsSettings = new();
	private RaytraceReflectionsPass m_reflectionsPass;

	public override void Create()
	{
		m_reflectionsPass = new RaytraceReflectionsPass(raytraceReflectionsSettings)
		{
			// After opaques + skybox: the composite is laid over the assembled frame.
			renderPassEvent = RenderPassEvent.AfterRenderingSkybox + 1
		};
	}

	public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
	{
		renderer.EnqueuePass(m_reflectionsPass);
	}
}
