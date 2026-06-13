using System;
using UnityEngine;
using UnityEngine.Rendering;

[ExecuteAlways]
public class RaytraceDataManager : MonoBehaviour
{
	[SerializeField] private LayerMask updateLayers = -1;
	[SerializeField] private bool m_rtasBuildEveryFrame = true;

	private RayTracingAccelerationStructure m_accelerationStructure;
	private bool m_aliveBetweenScenes = true;
	private bool m_rtSupport;
	private bool m_rtasReady;

	public LayerMask UpdateLayers => updateLayers;

#region Singleton
	public static RaytraceDataManager instance;

	private void Awake()
	{
		Debug.Log($"[RTDM] Awake on {gameObject.name}", this);
		if (instance != null &&  instance != this)
		{
			Destroy(gameObject);
			return;
		}

		instance = this;
#if !UNITY_EDITOR
		DontDestroyOnLoad(gameObject);
#endif
		Debug.Log("[RTDM] Instance assigned", this);
	}

#endregion

	public bool IsReady()
	{
		return m_rtasReady;
	}

	public bool TryGetRTAS(out RayTracingAccelerationStructure accelerationStructure)
	{
		accelerationStructure = m_rtasReady ? m_accelerationStructure : null;
		return m_rtasReady;
	}

	private void OnEnable()
	{
		Debug.Log("[RTDM] OnEnable", this);
		m_rtasReady = false;
		m_rtSupport = SystemInfo.supportsRayTracing;
		if (!m_rtSupport)
		{
			Debug.LogWarning("RayTracing support is not supported on this platform.");
			return;
		}

		if (Application.isPlaying && m_aliveBetweenScenes)
		{
			transform.SetParent(null);
			DontDestroyOnLoad(gameObject);
		}

		if (m_accelerationStructure == null)
		{
			RayTracingAccelerationStructure.Settings settings = new()
			{
				layerMask = updateLayers,
				managementMode = RayTracingAccelerationStructure.ManagementMode.Automatic,
				rayTracingModeMask = RayTracingAccelerationStructure.RayTracingModeMask.Everything
			};
			m_accelerationStructure = new RayTracingAccelerationStructure(settings);
		}
		m_accelerationStructure.Build();
	}

	private void LateUpdate()
	{
		if (m_rtSupport && m_rtasBuildEveryFrame)
		{
			m_accelerationStructure.Build();
		}
		m_rtasReady = true;
	}

	private void OnDestroy()
	{
		Debug.Log("[RTDM] OnDestroy", this);
		if(instance == this)
		{
			instance = null;
		}

		if (!m_rtSupport)
		{
			return;
		}

		Dispose();
	}

	private void Dispose()
	{
		if (m_rtasReady)
		{
			m_accelerationStructure.Release();
			m_accelerationStructure = null;
			m_rtasReady = false;
		}
	}


}
