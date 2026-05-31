using UnityEngine;
using UnityEngine.Rendering;

[CreateAssetMenu(fileName = "RayTraceConfig", menuName = "RayTracing/RayTraceConfig")]
public class RaytraceConfig : ScriptableObject
{
	[Header("Shadows Data")]
	public RayTracingShader shadowRayTracingShader;
	public ComputeShader shadowFilteringCs;
}
