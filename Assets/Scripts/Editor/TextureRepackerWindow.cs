using System.IO;
using UnityEditor;
using UnityEngine;

// Repacks third-party mask textures into the channel layout our Lit/RT shaders expect:
//   _MetallicGlossMap : R = metallic, A = smoothness (URP metallic workflow)
//   _OcclusionMap     : G = ambient occlusion
//   _BaseMap          : RGB = albedo, A = alpha (cutout)
// Asset-store packs use arbitrary layouts (ORM / RMA / MRA, roughness instead of
// smoothness, alpha in a separate texture) - this window unpacks/repacks them.
public class TextureRepackerWindow : EditorWindow
{
	private enum Channel { R = 0, G = 1, B = 2, A = 3 }
	private enum Tab { MaskUnpack, AlbedoAlphaPack }

	private struct Preset
	{
		public string name;
		public Channel ao, rough, metal;
		public Preset(string name, Channel ao, Channel rough, Channel metal)
		{
			this.name = name; this.ao = ao; this.rough = rough; this.metal = metal;
		}
	}

	// Letter order = channel order in the source texture (R, G, B).
	private static readonly Preset[] Presets =
	{
		new Preset("ORM / ARM  (R=AO, G=Rough, B=Metal)", Channel.R, Channel.G, Channel.B),
		new Preset("RMA  (R=Rough, G=Metal, B=AO)",       Channel.B, Channel.R, Channel.G),
		new Preset("MRA  (R=Metal, G=Rough, B=AO)",       Channel.B, Channel.G, Channel.R),
		new Preset("RMO  (R=Rough, G=Metal, B=AO=Occl)",  Channel.B, Channel.R, Channel.G),
		new Preset("Unity Mask Map / HDRP (R=Metal, G=AO, A=Smooth)", Channel.G, Channel.A, Channel.R),
	};

	private Tab m_tab;

	// --- Mask unpack state ---
	private Texture2D m_maskSource;
	private int m_presetIndex;
	private Channel m_metalChannel = Channel.B;
	private Channel m_roughChannel = Channel.G;
	private Channel m_aoChannel = Channel.R;
	private bool m_sourceIsRoughness = true; // invert into smoothness
	private bool m_writeMetallicSmoothness = true;
	private bool m_writeOcclusion = true;

	// --- Albedo+alpha pack state ---
	private Texture2D m_albedoSource;
	private Texture2D m_alphaSource;
	private Channel m_alphaChannel = Channel.R;

	[MenuItem("Akunaki/Texture Repacker")]
	private static void Open()
	{
		var w = GetWindow<TextureRepackerWindow>("Texture Repacker");
		w.minSize = new Vector2(380, 320);
	}

	private void OnGUI()
	{
		m_tab = (Tab)GUILayout.Toolbar((int)m_tab, new[] { "Mask Unpack", "Albedo + Alpha" });
		EditorGUILayout.Space(8);

		if (m_tab == Tab.MaskUnpack)
		{
			DrawMaskTab();
		}
		else
		{
			DrawAlbedoTab();
		}
	}

	// ------------------------------------------------------------------ mask tab

	private void DrawMaskTab()
	{
		EditorGUILayout.HelpBox(
			"Вход: упакованная маска (ORM/RMA/...). Выход: _MetallicSmoothness (R=metallic, A=smoothness) " +
			"и _Occlusion (G=AO) рядом с исходником.",
			MessageType.Info);

		m_maskSource = (Texture2D)EditorGUILayout.ObjectField("Source (packed mask)", m_maskSource, typeof(Texture2D), false);

		EditorGUILayout.Space(4);
		EditorGUI.BeginChangeCheck();
		m_presetIndex = EditorGUILayout.Popup("Preset", m_presetIndex, GetPresetNames());
		if (EditorGUI.EndChangeCheck())
		{
			var p = Presets[m_presetIndex];
			m_aoChannel = p.ao;
			m_roughChannel = p.rough;
			m_metalChannel = p.metal;
			// HDRP mask map stores smoothness, not roughness.
			m_sourceIsRoughness = m_presetIndex != 4;
		}

		EditorGUILayout.LabelField("Channel mapping (override per texture)", EditorStyles.boldLabel);
		m_metalChannel = (Channel)EditorGUILayout.EnumPopup("Metallic from", m_metalChannel);
		m_roughChannel = (Channel)EditorGUILayout.EnumPopup(m_sourceIsRoughness ? "Roughness from" : "Smoothness from", m_roughChannel);
		m_aoChannel = (Channel)EditorGUILayout.EnumPopup("AO from", m_aoChannel);
		m_sourceIsRoughness = EditorGUILayout.Toggle(new GUIContent("Source is roughness",
			"Вкл: канал хранит roughness, инвертируем в smoothness (A = 1 - rough). Выкл: канал уже smoothness."), m_sourceIsRoughness);

		EditorGUILayout.Space(4);
		m_writeMetallicSmoothness = EditorGUILayout.Toggle("Write _MetallicSmoothness", m_writeMetallicSmoothness);
		m_writeOcclusion = EditorGUILayout.Toggle("Write _Occlusion", m_writeOcclusion);

		EditorGUILayout.Space(8);
		using (new EditorGUI.DisabledScope(m_maskSource == null || (!m_writeMetallicSmoothness && !m_writeOcclusion)))
		{
			if (GUILayout.Button("Unpack", GUILayout.Height(28)))
			{
				UnpackMask();
			}
		}
	}

	private static string[] GetPresetNames()
	{
		var names = new string[Presets.Length];
		for (int i = 0; i < Presets.Length; i++) names[i] = Presets[i].name;
		return names;
	}

	private void UnpackMask()
	{
		var src = ReadPixels(m_maskSource, out int w, out int h);
		if (src == null)
		{
			return;
		}

		string srcPath = AssetDatabase.GetAssetPath(m_maskSource);
		string dir = Path.GetDirectoryName(srcPath).Replace('\\', '/');
		string baseName = Path.GetFileNameWithoutExtension(srcPath);

		if (m_writeMetallicSmoothness)
		{
			var pixels = new Color32[src.Length];
			for (int i = 0; i < src.Length; i++)
			{
				byte metal = GetChannel(src[i], m_metalChannel);
				byte smooth = GetChannel(src[i], m_roughChannel);
				if (m_sourceIsRoughness)
				{
					smooth = (byte)(255 - smooth);
				}
				// R = metallic, A = smoothness; G/B unused (kept = metallic for preview readability).
				pixels[i] = new Color32(metal, metal, metal, smooth);
			}
			string path = $"{dir}/{baseName}_MetallicSmoothness.png";
			WritePng(pixels, w, h, path, sRGB: false, alphaIsTransparency: false);
		}

		if (m_writeOcclusion)
		{
			var pixels = new Color32[src.Length];
			for (int i = 0; i < src.Length; i++)
			{
				byte ao = GetChannel(src[i], m_aoChannel);
				// URP samples occlusion from G; grayscale keeps it readable in the inspector.
				pixels[i] = new Color32(ao, ao, ao, 255);
			}
			string path = $"{dir}/{baseName}_Occlusion.png";
			WritePng(pixels, w, h, path, sRGB: false, alphaIsTransparency: false);
		}

		AssetDatabase.Refresh();
		Debug.Log($"[TextureRepacker] Unpacked '{baseName}' -> {dir}", m_maskSource);
	}


	private void DrawAlbedoTab()
	{
		EditorGUILayout.HelpBox(
			"Вход: albedo (RGB) + отдельная alpha-текстура. Выход: одна RGBA _BaseMap рядом с albedo. " +
			"Размеры могут не совпадать - alpha ресемплится билинейно под размер albedo.",
			MessageType.Info);

		m_albedoSource = (Texture2D)EditorGUILayout.ObjectField("Albedo (RGB)", m_albedoSource, typeof(Texture2D), false);
		m_alphaSource = (Texture2D)EditorGUILayout.ObjectField("Alpha texture", m_alphaSource, typeof(Texture2D), false);
		m_alphaChannel = (Channel)EditorGUILayout.EnumPopup("Alpha from channel", m_alphaChannel);

		EditorGUILayout.Space(8);
		using (new EditorGUI.DisabledScope(m_albedoSource == null || m_alphaSource == null))
		{
			if (GUILayout.Button("Pack", GUILayout.Height(28)))
			{
				PackAlbedoAlpha();
			}
		}
	}

	private void PackAlbedoAlpha()
	{
		var albedo = ReadPixels(m_albedoSource, out int w, out int h);
		var alphaTex = ReadableCopy(m_alphaSource); // bilinear resample needs a texture, not an array
		if (albedo == null || alphaTex == null)
		{
			return;
		}

		var pixels = new Color32[albedo.Length];
		for (int y = 0; y < h; y++)
		{
			for (int x = 0; x < w; x++)
			{
				int i = y * w + x;
				Color a = alphaTex.GetPixelBilinear((x + 0.5f) / w, (y + 0.5f) / h);
				byte alpha = (byte)Mathf.RoundToInt(GetChannel(a, m_alphaChannel) * 255f);
				pixels[i] = new Color32(albedo[i].r, albedo[i].g, albedo[i].b, alpha);
			}
		}
		DestroyImmediate(alphaTex);

		string srcPath = AssetDatabase.GetAssetPath(m_albedoSource);
		string dir = Path.GetDirectoryName(srcPath).Replace('\\', '/');
		string path = $"{dir}/{Path.GetFileNameWithoutExtension(srcPath)}_BaseMap.png";
		WritePng(pixels, w, h, path, sRGB: true, alphaIsTransparency: true);

		AssetDatabase.Refresh();
		Debug.Log($"[TextureRepacker] Packed albedo+alpha -> {path}", m_albedoSource);
	}


	private static byte GetChannel(Color32 c, Channel ch)
	{
		switch (ch)
		{
			case Channel.R: return c.r;
			case Channel.G: return c.g;
			case Channel.B: return c.b;
			default: return c.a;
		}
	}

	private static float GetChannel(Color c, Channel ch)
	{
		switch (ch)
		{
			case Channel.R: return c.r;
			case Channel.G: return c.g;
			case Channel.B: return c.b;
			default: return c.a;
		}
	}

	// Raw stored pixel values regardless of compression/readability: temporarily flip
	// the importer to readable+uncompressed, copy, then restore import settings.
	private static Color32[] ReadPixels(Texture2D tex, out int w, out int h)
	{
		var copy = ReadableCopy(tex);
		if (copy == null)
		{
			w = h = 0;
			return null;
		}
		w = copy.width;
		h = copy.height;
		var pixels = copy.GetPixels32();
		DestroyImmediate(copy);
		return pixels;
	}

	private static Texture2D ReadableCopy(Texture2D tex)
	{
		string path = AssetDatabase.GetAssetPath(tex);
		var importer = AssetImporter.GetAtPath(path) as TextureImporter;
		if (importer == null)
		{
			Debug.LogError($"[TextureRepacker] '{tex.name}' is not a texture asset.");
			return null;
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

		var copy = new Texture2D(tex.width, tex.height, TextureFormat.RGBA32, false, linear: true);
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

	private static void WritePng(Color32[] pixels, int w, int h, string path, bool sRGB, bool alphaIsTransparency)
	{
		var tex = new Texture2D(w, h, TextureFormat.RGBA32, false, linear: !sRGB);
		tex.SetPixels32(pixels);
		tex.Apply();
		File.WriteAllBytes(path, tex.EncodeToPNG());
		DestroyImmediate(tex);

		AssetDatabase.ImportAsset(path);
		var importer = (TextureImporter)AssetImporter.GetAtPath(path);
		importer.sRGBTexture = sRGB;
		importer.alphaIsTransparency = alphaIsTransparency;
		importer.SaveAndReimport();
	}
}
