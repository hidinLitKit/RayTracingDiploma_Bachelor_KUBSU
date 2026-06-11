using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public class RaytraceShadowsRendererFeature : RaytraceFeatureBase
{
	[Serializable]
	public class RaytraceShadowsFeatureSettings : RaytraceFeatureSettings
	{
		[Header("Shadow settings")]
		[Range(0f, 0.5f)]   public float shadowBias = 0.001f;
		[Range(0f, 1f)]         public float shadowStrength = 1f;
		[Range(1, 32)]          public int shadowCastCount = 16;
		public bool smoothShadows = false;

		[Header("PCSS (contact-hardening soft shadows)")]
		[Range(0.05f, 10f)]     public float lightAngularRadius = 1.0f;
		[Range(1, 32)]          public int pcssMaxRadius = 16;
		[Range(0.005f, 0.5f)]   public float pcssDepthRejection = 0.05f;

		[Header("Temporal accumulation (camera reprojection)")]
		[Tooltip("Accumulate shadows over frames. Lets shadowCastCount drop while staying stable in motion.")]
		public bool temporalEnabled = true;
		[Range(0.02f, 1f), Tooltip("Weight of the current frame. Lower = smoother / more lag.")]
		public float temporalAlpha = 0.1f;
		[Range(0f, 1f), Tooltip("Reject history when its depth differs by more than this (relative). 0 = off.")]
		public float temporalDepthReject = 0.3f;
	}

	public RaytraceShadowsFeatureSettings raytraceShadowsSettings = new();
	private RaytraceShadowsPass m_shadowsPass;

	// Persistent ping-pong history for temporal accumulation.
	private RTHandle m_historyA;
	private RTHandle m_historyB;
	private bool m_pingPong;
	private int m_historyWidth;
	private int m_historyHeight;
	private bool m_historyReset;

	public override void Create()
	{
		m_shadowsPass = new RaytraceShadowsPass(raytraceShadowsSettings)
		{
			renderPassEvent = RenderPassEvent.BeforeRenderingOpaques
		};
	}

	public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
	{
		if (raytraceShadowsSettings.temporalEnabled)
		{
			var desc = renderingData.cameraData.cameraTargetDescriptor;
			int scale = Mathf.Max(1, raytraceShadowsSettings.raySpawnScale);
			int w = Mathf.Max(1, desc.width / scale);
			int h = Mathf.Max(1, desc.height / scale);

			EnsureHistory(w, h);

			RTHandle read = m_pingPong ? m_historyA : m_historyB;
			RTHandle write = m_pingPong ? m_historyB : m_historyA;
			m_shadowsPass.SetTemporalHistory(read, write, m_historyReset);

			m_pingPong = !m_pingPong;
			m_historyReset = false;
		}
		else
		{
			m_shadowsPass.SetTemporalHistory(null, null, true);
			m_historyReset = true;
		}

		renderer.EnqueuePass(m_shadowsPass);
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
		m_historyA = RTHandles.Alloc(w, h, colorFormat: format, enableRandomWrite: true, filterMode: FilterMode.Bilinear, name: "_ShadowHistoryA");
		m_historyB = RTHandles.Alloc(w, h, colorFormat: format, enableRandomWrite: true, filterMode: FilterMode.Bilinear, name: "_ShadowHistoryB");

		m_historyWidth = w;
		m_historyHeight = h;
		m_historyReset = true;
	}

	protected override void Dispose(bool disposing)
	{
		m_historyA?.Release();
		m_historyB?.Release();
		m_historyA = null;
		m_historyB = null;
	}
}
