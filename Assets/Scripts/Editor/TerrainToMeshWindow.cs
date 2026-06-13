using System.IO;
using UnityEditor;
using UnityEngine;

// Converts a Unity Terrain into a regular static mesh so it participates in ray tracing
// Output (next to the TerrainData asset): <name>_Mesh.asset, <name>_Albedo.png,
// <name>_Mat.mat, plus a new GameObject in the scene. Optionally disables the Terrain.
public class TerrainToMeshWindow : EditorWindow
{
	private const string LitShaderName = "Universal Render Pipeline/Lit (with rtx)";

	private Terrain m_terrain;
	private int m_meshResolution = 128;  // quads per side
	private int m_bakeResolution = 1024; // baked albedo size
	private float m_smoothness = 0.1f;   // matte ground
	private bool m_disableSourceTerrain = true;

	[MenuItem("Akunaki/Terrain To Mesh")]
	private static void Open()
	{
		var w = GetWindow<TerrainToMeshWindow>("Terrain To Mesh");
		w.minSize = new Vector2(380, 240);
	}

	private void OnGUI()
	{
		EditorGUILayout.HelpBox(
			"Конвертирует Terrain в статический меш + запекает splat-слои в одну albedo. " +
			"Меш получает наш Lit (with rtx) и попадает в RTAS: тени, отражения, волуметрики начинают видеть землю.",
			MessageType.Info);

		m_terrain = (Terrain)EditorGUILayout.ObjectField("Terrain", m_terrain, typeof(Terrain), true);
		m_meshResolution = EditorGUILayout.IntSlider(new GUIContent("Mesh resolution",
			"Квадов на сторону. 128 -> ~33k вершин, 256 -> ~132k. Больше = точнее рельеф, тяжелее RTAS."),
			m_meshResolution, 32, 512);
		m_bakeResolution = EditorGUILayout.IntPopup("Albedo bake size", m_bakeResolution,
			new[] { "512", "1024", "2048", "4096" }, new[] { 512, 1024, 2048, 4096 });
		m_smoothness = EditorGUILayout.Slider("Material smoothness", m_smoothness, 0f, 1f);
		m_disableSourceTerrain = EditorGUILayout.Toggle(new GUIContent("Disable source terrain",
			"Выключить исходный Terrain после экспорта. Внимание: пропадут terrain-деревья и detail-трава, если они есть."),
			m_disableSourceTerrain);

		EditorGUILayout.Space(8);
		using (new EditorGUI.DisabledScope(m_terrain == null))
		{
			if (GUILayout.Button("Convert", GUILayout.Height(28)))
			{
				Convert();
			}
		}
	}

	private void Convert()
	{
		var data = m_terrain.terrainData;
		string dataPath = AssetDatabase.GetAssetPath(data);
		string dir = string.IsNullOrEmpty(dataPath) ? "Assets" : Path.GetDirectoryName(dataPath).Replace('\\', '/');
		string baseName = m_terrain.name;

		try
		{
			EditorUtility.DisplayProgressBar("Terrain To Mesh", "Building mesh...", 0.1f);
			Mesh mesh = BuildMesh(data);

			EditorUtility.DisplayProgressBar("Terrain To Mesh", "Baking albedo...", 0.4f);
			Texture2D albedo = BakeAlbedo(data, m_bakeResolution);

			EditorUtility.DisplayProgressBar("Terrain To Mesh", "Saving assets...", 0.8f);

			string meshPath = $"{dir}/{baseName}_Mesh.asset";
			AssetDatabase.CreateAsset(mesh, meshPath);

			string texPath = $"{dir}/{baseName}_Albedo.png";
			File.WriteAllBytes(texPath, albedo.EncodeToPNG());
			DestroyImmediate(albedo);
			AssetDatabase.ImportAsset(texPath);
			var texImporter = AssetImporter.GetAtPath(texPath) as TextureImporter;
			if (texImporter != null)
			{
				texImporter.sRGBTexture = true;
				texImporter.maxTextureSize = 4096;
				texImporter.SaveAndReimport();
			}

			var shader = Shader.Find(LitShaderName);
			if (shader == null)
			{
				Debug.LogError($"[TerrainToMesh] Shader '{LitShaderName}' not found.");
				return;
			}
			var mat = new Material(shader);
			mat.SetTexture("_BaseMap", AssetDatabase.LoadAssetAtPath<Texture2D>(texPath));
			mat.SetFloat("_Metallic", 0f);
			mat.SetFloat("_Smoothness", m_smoothness);
			string matPath = $"{dir}/{baseName}_Mat.mat";
			AssetDatabase.CreateAsset(mat, matPath);

			var go = new GameObject($"{baseName}_Mesh");
			go.transform.position = m_terrain.GetPosition();
			go.AddComponent<MeshFilter>().sharedMesh = mesh;
			go.AddComponent<MeshRenderer>().sharedMaterial = mat;
			go.AddComponent<MeshCollider>().sharedMesh = mesh;
			go.isStatic = true;
			Undo.RegisterCreatedObjectUndo(go, "Terrain To Mesh");

			if (m_disableSourceTerrain)
			{
				Undo.RecordObject(m_terrain.gameObject, "Disable terrain");
				m_terrain.gameObject.SetActive(false);
			}

			AssetDatabase.SaveAssets();
			Selection.activeGameObject = go;
			Debug.Log($"[TerrainToMesh] Done: {meshPath} ({mesh.vertexCount} verts), {texPath}, {matPath}", go);
		}
		finally
		{
			EditorUtility.ClearProgressBar();
		}
	}

	// Vertex grid sampled from the heightmap; normals from the terrain itself.
	private Mesh BuildMesh(TerrainData data)
	{
		int res = m_meshResolution;
		int vertsPerSide = res + 1;
		Vector3 size = data.size;

		var vertices = new Vector3[vertsPerSide * vertsPerSide];
		var normals = new Vector3[vertices.Length];
		var uvs = new Vector2[vertices.Length];

		for (int z = 0; z <= res; z++)
		{
			for (int x = 0; x <= res; x++)
			{
				int i = z * vertsPerSide + x;
				float u = (float)x / res;
				float v = (float)z / res;

				float height = data.GetInterpolatedHeight(u, v);
				vertices[i] = new Vector3(u * size.x, height, v * size.z);
				normals[i] = data.GetInterpolatedNormal(u, v);
				uvs[i] = new Vector2(u, v); // planar UV matches the baked albedo
			}
		}

		var triangles = new int[res * res * 6];
		int t = 0;
		for (int z = 0; z < res; z++)
		{
			for (int x = 0; x < res; x++)
			{
				int a = z * vertsPerSide + x;          // (x,   z)
				int b = (z + 1) * vertsPerSide + x;    // (x,   z+1)
				int c = a + 1;                         // (x+1, z)
				int d = b + 1;                         // (x+1, z+1)

				triangles[t++] = a; triangles[t++] = b; triangles[t++] = c;
				triangles[t++] = c; triangles[t++] = b; triangles[t++] = d;
			}
		}

		var mesh = new Mesh
		{
			name = data.name + "_Mesh",
			indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 // > 65k verts at high resolutions
		};
		mesh.vertices = vertices;
		mesh.normals = normals;
		mesh.uv = uvs;
		mesh.triangles = triangles;
		mesh.RecalculateBounds();
		mesh.RecalculateTangents();
		return mesh;
	}

	// CPU splat blend: albedo(uv) = sum over layers of weight(uv) * layerDiffuse(tiled uv).
	private Texture2D BakeAlbedo(TerrainData data, int res)
	{
		float[,,] weights = data.GetAlphamaps(0, 0, data.alphamapWidth, data.alphamapHeight);
		int aw = data.alphamapWidth;
		int ah = data.alphamapHeight;
		var layers = data.terrainLayers;
		Vector3 size = data.size;

		// Readable copies of the layer diffuse textures (asset textures are usually compressed/non-readable).
		var diffuse = new Texture2D[layers.Length];
		for (int l = 0; l < layers.Length; l++)
		{
			diffuse[l] = layers[l].diffuseTexture != null ? ReadableCopy(layers[l].diffuseTexture) : null;
		}

		var result = new Texture2D(res, res, TextureFormat.RGBA32, false);
		var pixels = new Color32[res * res];

		for (int y = 0; y < res; y++)
		{
			float v = (y + 0.5f) / res;
			for (int x = 0; x < res; x++)
			{
				float u = (x + 0.5f) / res;
				Color acc = Color.black;

				for (int l = 0; l < layers.Length; l++)
				{
					float w = SampleWeight(weights, aw, ah, u, v, l);
					if (w < 0.003f || diffuse[l] == null)
					{
						continue;
					}

					// World-space tiling, same as the terrain shader.
					Vector2 tile = layers[l].tileSize;
					Vector2 offset = layers[l].tileOffset;
					float tu = (u * size.x + offset.x) / Mathf.Max(0.001f, tile.x);
					float tv = (v * size.z + offset.y) / Mathf.Max(0.001f, tile.y);
					acc += diffuse[l].GetPixelBilinear(tu - Mathf.Floor(tu), tv - Mathf.Floor(tv)) * w;
				}

				acc.a = 1f;
				pixels[y * res + x] = acc;
			}
		}

		for (int l = 0; l < diffuse.Length; l++)
		{
			if (diffuse[l] != null)
			{
				DestroyImmediate(diffuse[l]);
			}
		}

		result.SetPixels32(pixels);
		result.Apply();
		return result;
	}

	// Bilinear sample of the splat-weight array for one layer.
	private static float SampleWeight(float[,,] weights, int w, int h, float u, float v, int layer)
	{
		float fx = Mathf.Clamp(u * (w - 1), 0, w - 1.001f);
		float fy = Mathf.Clamp(v * (h - 1), 0, h - 1.001f);
		int x0 = (int)fx, y0 = (int)fy;
		float tx = fx - x0, ty = fy - y0;

		float a = weights[y0, x0, layer];
		float b = weights[y0, x0 + 1, layer];
		float c = weights[y0 + 1, x0, layer];
		float d = weights[y0 + 1, x0 + 1, layer];
		return Mathf.Lerp(Mathf.Lerp(a, b, tx), Mathf.Lerp(c, d, tx), ty);
	}

	private static Texture2D ReadableCopy(Texture2D tex)
	{
		string path = AssetDatabase.GetAssetPath(tex);
		var importer = AssetImporter.GetAtPath(path) as TextureImporter;
		if (importer == null)
		{
			// Built-in / runtime texture: try a GPU blit instead.
			var rt = RenderTexture.GetTemporary(tex.width, tex.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
			Graphics.Blit(tex, rt);
			var prev = RenderTexture.active;
			RenderTexture.active = rt;
			var blitCopy = new Texture2D(tex.width, tex.height, TextureFormat.RGBA32, false);
			blitCopy.ReadPixels(new Rect(0, 0, tex.width, tex.height), 0, 0);
			blitCopy.Apply();
			RenderTexture.active = prev;
			RenderTexture.ReleaseTemporary(rt);
			return blitCopy;
		}

		bool wasReadable = importer.isReadable;
		var wasCompression = importer.textureCompression;
		bool dirty = false;

		if (!wasReadable || wasCompression != TextureImporterCompression.Uncompressed)
		{
			importer.isReadable = true;
			importer.textureCompression = TextureImporterCompression.Uncompressed;
			importer.SaveAndReimport();
			dirty = true;
		}

		var copy = new Texture2D(tex.width, tex.height, TextureFormat.RGBA32, false);
		copy.SetPixels32(tex.GetPixels32());
		copy.Apply();

		if (dirty)
		{
			importer.isReadable = wasReadable;
			importer.textureCompression = wasCompression;
			importer.SaveAndReimport();
		}

		return copy;
	}
}
