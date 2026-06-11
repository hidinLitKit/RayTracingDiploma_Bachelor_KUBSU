using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public class RaytraceVolumetricRendererFeature : RaytraceFeatureBase
{
	[Serializable]
	public class RaytraceVolumetricFeatureSettings : RaytraceFeatureSettings
	{
		[Header("Volumetric / god rays settings")]
		[Tooltip("Tint of the in-scattered light.")]
		public Color scatterColor = Color.white;
		[Range(0f, 0.5f), Tooltip("Fog thickness. Drives both how much light scatters in and how fast distant fog fades.")]
		public float density = 0.05f;
		[Range(0f, 0.95f), Tooltip("Phase anisotropy. 0 = uniform glow, 0.6-0.8 = sharp god rays / halo toward the sun.")]
		public float phaseG = 0.7f;
		[Range(0f, 10f), Tooltip("Overall brightness of the effect.")]
		public float fogIntensity = 1f;
		[Range(1f, 500f), Tooltip("Max march distance for sky pixels (geometry clamps it shorter).")]
		public float maxFogDistance = 60f;
		[Range(4, 128), Tooltip("Ray-march steps per pixel. Main cost; jitter + blur hide low counts.")]
		public int stepCount = 32;

		[Header("Denoise")]
		[Range(0, 4), Tooltip("Bilateral blur radius (pixels) over the low-res fog. 0 disables denoise.")]
		public int blurRadius = 2;
		[Range(0.02f, 0.5f), Tooltip("Bilateral depth rejection: lower = sharper edges, less bleeding across silhouettes.")]
		public float blurDepthReject = 0.1f;

		[Header("Temporal accumulation (Phase 1: camera reprojection)")]
		[Tooltip("Accumulate the fog over frames with reprojection. Stable under camera motion.")]
		public bool temporalEnabled = true;
		[Range(0.02f, 1f), Tooltip("Weight of the current frame. Lower = smoother / more lag. 1 = no accumulation.")]
		public float temporalAlpha = 0.1f;
		[Range(0f, 1f), Tooltip("Reject history when its depth differs by more than this (relative). 0 = off. Lower = less ghosting on edges, more disocclusion noise.")]
		public float temporalDepthReject = 0.3f;
	}

	public RaytraceVolumetricFeatureSettings raytraceVolumetricSettings = new();
	private RaytraceVolumetricPass m_volumetricPass;

	// Persistent ping-pong history for temporal accumulation (must survive across frames).
	private RTHandle m_historyA;
	private RTHandle m_historyB;
	private bool m_pingPong;
	private int m_historyWidth;
	private int m_historyHeight;
	private bool m_historyReset;

	public override void Create()
	{
		m_volumetricPass = new RaytraceVolumetricPass(raytraceVolumetricSettings)
		{
			// After opaques + skybox: needs the camera depth texture and composites over the frame.
			renderPassEvent = RenderPassEvent.AfterRenderingTransparents
		};
	}

	public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
	{
		if (raytraceVolumetricSettings.temporalEnabled)
		{
			var desc = renderingData.cameraData.cameraTargetDescriptor;
			int scale = Mathf.Max(1, raytraceVolumetricSettings.raySpawnScale);
			int w = Mathf.Max(1, desc.width / scale);
			int h = Mathf.Max(1, desc.height / scale);

			EnsureHistory(w, h);

			RTHandle read = m_pingPong ? m_historyA : m_historyB;
			RTHandle write = m_pingPong ? m_historyB : m_historyA;
			m_volumetricPass.SetTemporalHistory(read, write, m_historyReset);

			m_pingPong = !m_pingPong;
			m_historyReset = false;
		}
		else
		{
			m_volumetricPass.SetTemporalHistory(null, null, true);
			m_historyReset = true; // re-init when re-enabled
		}

		renderer.EnqueuePass(m_volumetricPass);
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
		m_historyA = RTHandles.Alloc(w, h, colorFormat: format, enableRandomWrite: true, filterMode: FilterMode.Bilinear, name: "_VolHistoryA");
		m_historyB = RTHandles.Alloc(w, h, colorFormat: format, enableRandomWrite: true, filterMode: FilterMode.Bilinear, name: "_VolHistoryB");

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
