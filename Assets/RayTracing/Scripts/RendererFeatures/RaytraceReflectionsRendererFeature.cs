using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
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

		[Header("Denoise - spatial")]
		[Range(0, 4), Tooltip("Spatial blur strength (pixels) on the low-res reflection. Reduces shimmer/flicker under motion. 0 = off (sharper but flickers).")]
		public int blurRadius = 1;
		[Range(0.02f, 1f), Tooltip("Bilateral depth rejection for the blur: lower = sharper edges, less bleeding.")]
		public float blurDepthReject = 0.3f;

		[Header("Denoise - temporal (reflection reprojection)")]
		[Tooltip("Accumulate the reflection over frames using unfolded-ray reprojection. Stabilizes low-res flat-mirror reflections (water). Reflected moving content lags slightly.")]
		public bool temporalEnabled = true;
		[Range(0.02f, 1f), Tooltip("Weight of the current frame. Lower = smoother / more lag. 1 = no accumulation.")]
		public float temporalAlpha = 0.1f;
		[Range(0f, 1f), Tooltip("Reject history when the reflection path length differs by more than this (relative). Lower = less ghosting on reflection disocclusion, more noise. 0 = off.")]
		public float temporalReject = 0.1f;
	}

	public RaytraceReflectionsFeatureSettings raytraceReflectionsSettings = new();
	private RaytraceReflectionsPass m_reflectionsPass;

	// Persistent ping-pong history for temporal accumulation (must survive across frames).
	private RTHandle m_historyA;
	private RTHandle m_historyB;
	private bool m_pingPong;
	private int m_historyWidth;
	private int m_historyHeight;
	private bool m_historyReset;

	public override void Create()
	{
		m_reflectionsPass = new RaytraceReflectionsPass(raytraceReflectionsSettings)
		{
			// After opaques + skybox: the composite is laid over the assembled frame.
			renderPassEvent = RenderPassEvent.AfterRenderingTransparents + 1
		};
	}

	public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
	{
		if (raytraceReflectionsSettings.temporalEnabled)
		{
			var desc = renderingData.cameraData.cameraTargetDescriptor;
			int scale = Mathf.Max(1, raytraceReflectionsSettings.raySpawnScale);
			int w = Mathf.Max(1, desc.width / scale);
			int h = Mathf.Max(1, desc.height / scale);

			EnsureHistory(w, h);

			RTHandle read = m_pingPong ? m_historyA : m_historyB;
			RTHandle write = m_pingPong ? m_historyB : m_historyA;
			m_reflectionsPass.SetTemporalHistory(read, write, m_historyReset);

			m_pingPong = !m_pingPong;
			m_historyReset = false;
		}
		else
		{
			m_reflectionsPass.SetTemporalHistory(null, null, true);
			m_historyReset = true; // re-init when re-enabled
		}

		renderer.EnqueuePass(m_reflectionsPass);
	}

	private void EnsureHistory(int w, int h)
	{
		if (m_historyA != null && m_historyWidth == w && m_historyHeight == h)
		{
			return;
		}

		m_historyA?.Release();
		m_historyB?.Release();

		var format = GraphicsFormat.R16G16B16A16_SFloat;
		m_historyA = RTHandles.Alloc(w, h, colorFormat: format, enableRandomWrite: true, filterMode: FilterMode.Bilinear, name: "_ReflHistoryA");
		m_historyB = RTHandles.Alloc(w, h, colorFormat: format, enableRandomWrite: true, filterMode: FilterMode.Bilinear, name: "_ReflHistoryB");

		m_historyWidth = w;
		m_historyHeight = h;
		m_historyReset = true; // fresh buffers -> first frame uses current only
	}

	protected override void Dispose(bool disposing)
	{
		m_historyA?.Release();
		m_historyB?.Release();
		m_historyA = null;
		m_historyB = null;
	}
}
