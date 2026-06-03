using UnityEngine;
using UnityEngine.Rendering;

[CreateAssetMenu(fileName = "RayTraceConfig", menuName = "RayTracing/RayTraceConfig")]
public class RaytraceConfig : ScriptableObject
{
	[Header("Shadows Data")]
	public RayTracingShader shadowRayTracingShader;
	public ComputeShader shadowFilteringCs;

	[Header("Reflections Data")]
	public RayTracingShader reflectionRayTracingShader;
	public Material reflectionApplyMaterial;
	public Texture fallbackSkybox;
}
