using UnityEngine;
using UnityEngine.Rendering;

[CreateAssetMenu(fileName = "RayTraceConfig", menuName = "RayTracing/RayTraceConfig")]
public class RaytraceConfig : ScriptableObject
{
	[Header("Common")]
	public Material blitMaterial;
	[Header("Shadows Data")]
	public RayTracingShader shadowRayTracingShader;
	public ComputeShader shadowFilteringCs;
}
