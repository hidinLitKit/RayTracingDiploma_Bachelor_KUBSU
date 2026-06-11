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
	public ComputeShader reflectionBlurCs;

	[Header("Volumetrics Data")]
	public RayTracingShader volumetricRayTracingShader;
	public Material volumetricApplyMaterial;
	public ComputeShader volumetricBlurCs;
	public ComputeShader temporalResolveCs;

	[Header("RT Optimizations (shadow + volumetric occlusion)")]
	[Tooltip("Occlusion rays accept alpha-tested geometry past this distance as opaque, skipping the cutout test. Big win for dense/distant foliage. 0 = always test.")]
	public float anyHitFarCutout = 25f;
	[Tooltip("Mip level for the cutout alpha sample in occlusion any-hit. Higher = cheaper but erodes thin leaves. 0 = full res.")]
	public float anyHitAlphaMip = 0f;
}
