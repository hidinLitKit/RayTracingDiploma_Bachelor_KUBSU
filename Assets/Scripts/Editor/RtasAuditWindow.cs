using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

// Scene audit for the ray tracing acceleration structure
public class RtasAuditWindow : EditorWindow
{
	private class Group
	{
		public string meshName;
		public int trisPerInstance;
		public int instanceCount;
		public long totalTris;
		public bool alphaTest;
		public string shaderName;
		public List<MeshRenderer> renderers = new List<MeshRenderer>();
		public bool selected; // checkbox state
	}

	private List<Group> m_groups = new List<Group>();
	private Vector2 m_scroll;
	private int m_targetLayer;
	private long m_sceneTotalTris;

	[MenuItem("Akunaki/RTAS Audit")]
	private static void Open()
	{
		var w = GetWindow<RtasAuditWindow>("RTAS Audit");
		w.minSize = new Vector2(640, 400);
	}

	private void OnGUI()
	{
		EditorGUILayout.HelpBox(
			"Группирует MeshRenderer'ы сцены по мешу и сортирует по суммарным треугольникам. " +
			"Кандидаты на исключение из RTAS: много инстансов + alpha-test (трава, кусты, мелкий реквизит). ",
			MessageType.Info);

		using (new EditorGUILayout.HorizontalScope())
		{
			if (GUILayout.Button("Scan scene", GUILayout.Height(24), GUILayout.Width(120)))
			{
				Scan();
			}
			GUILayout.FlexibleSpace();
			if (m_sceneTotalTris > 0)
			{
				GUILayout.Label($"Total: {m_sceneTotalTris:N0} tris", EditorStyles.boldLabel);
			}
		}

		if (m_groups.Count == 0)
		{
			return;
		}

		// Header
		using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
		{
			GUILayout.Label("", GUILayout.Width(20));
			GUILayout.Label("Mesh", EditorStyles.miniBoldLabel, GUILayout.MinWidth(160));
			GUILayout.Label("Inst", EditorStyles.miniBoldLabel, GUILayout.Width(45));
			GUILayout.Label("Tris/inst", EditorStyles.miniBoldLabel, GUILayout.Width(70));
			GUILayout.Label("Total tris", EditorStyles.miniBoldLabel, GUILayout.Width(80));
			GUILayout.Label("%", EditorStyles.miniBoldLabel, GUILayout.Width(45));
			GUILayout.Label("Alpha", EditorStyles.miniBoldLabel, GUILayout.Width(40));
			GUILayout.Label("Layer", EditorStyles.miniBoldLabel, GUILayout.Width(90));
			GUILayout.Label("", GUILayout.Width(50));
		}

		m_scroll = EditorGUILayout.BeginScrollView(m_scroll);
		foreach (var g in m_groups)
		{
			using (new EditorGUILayout.HorizontalScope())
			{
				g.selected = EditorGUILayout.Toggle(g.selected, GUILayout.Width(20));
				GUILayout.Label(g.meshName, GUILayout.MinWidth(160));
				GUILayout.Label(g.instanceCount.ToString(), GUILayout.Width(45));
				GUILayout.Label(g.trisPerInstance.ToString("N0"), GUILayout.Width(70));
				GUILayout.Label(g.totalTris.ToString("N0"), GUILayout.Width(80));
				GUILayout.Label(((float)g.totalTris / m_sceneTotalTris * 100f).ToString("F1"), GUILayout.Width(45));
				GUILayout.Label(g.alphaTest ? "YES" : "-", g.alphaTest ? EditorStyles.boldLabel : EditorStyles.label, GUILayout.Width(40));
				GUILayout.Label(GetGroupLayerName(g), GUILayout.Width(90));
				if (GUILayout.Button("Select", EditorStyles.miniButton, GUILayout.Width(50)))
				{
					Selection.objects = g.renderers.Where(r => r != null).Select(r => (Object)r.gameObject).ToArray();
				}
			}
		}
		EditorGUILayout.EndScrollView();

		EditorGUILayout.Space(4);

		long checkedTris = m_groups.Where(g => g.selected).Sum(g => g.totalTris);
		using (new EditorGUILayout.HorizontalScope())
		{
			if (GUILayout.Button("Check alpha-test", GUILayout.Width(110)))
			{
				foreach (var g in m_groups) g.selected = g.alphaTest;
			}
			if (GUILayout.Button("Uncheck all", GUILayout.Width(90)))
			{
				foreach (var g in m_groups) g.selected = false;
			}
			GUILayout.FlexibleSpace();
			GUILayout.Label($"Checked: {checkedTris:N0} tris ({(m_sceneTotalTris > 0 ? (float)checkedTris / m_sceneTotalTris * 100f : 0f):F1}%)");
		}

		using (new EditorGUILayout.HorizontalScope())
		{
			m_targetLayer = EditorGUILayout.LayerField("Move checked to layer", m_targetLayer);
			using (new EditorGUI.DisabledScope(checkedTris == 0))
			{
				if (GUILayout.Button("Apply", GUILayout.Width(80)))
				{
					MoveCheckedToLayer();
				}
			}
		}
	}

	private void Scan()
	{
		m_groups.Clear();
		m_sceneTotalTris = 0;

		var byMesh = new Dictionary<Mesh, Group>();
		var renderers = FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None);

		foreach (var r in renderers)
		{
			if (!r.enabled || !r.gameObject.activeInHierarchy)
			{
				continue;
			}
			var mf = r.GetComponent<MeshFilter>();
			if (mf == null || mf.sharedMesh == null)
			{
				continue;
			}

			Mesh mesh = mf.sharedMesh;
			if (!byMesh.TryGetValue(mesh, out var g))
			{
				int tris = 0;
				for (int s = 0; s < mesh.subMeshCount; s++)
				{
					tris += (int)mesh.GetIndexCount(s) / 3;
				}
				g = new Group
				{
					meshName = mesh.name,
					trisPerInstance = tris,
					alphaTest = IsAlphaTested(r),
					shaderName = r.sharedMaterial != null && r.sharedMaterial.shader != null ? r.sharedMaterial.shader.name : "?",
				};
				byMesh.Add(mesh, g);
			}
			g.instanceCount++;
			g.totalTris += g.trisPerInstance;
			g.renderers.Add(r);
		}

		m_groups = byMesh.Values.OrderByDescending(g => g.totalTris).ToList();
		foreach (var g in m_groups)
		{
			m_sceneTotalTris += g.totalTris;
		}
	}

	private static bool IsAlphaTested(MeshRenderer r)
	{
		foreach (var mat in r.sharedMaterials)
		{
			if (mat == null)
			{
				continue;
			}
			if (mat.IsKeywordEnabled("_ALPHATEST_ON"))
			{
				return true;
			}
			if (mat.HasProperty("_AlphaClip") && mat.GetFloat("_AlphaClip") > 0.5f)
			{
				return true;
			}
		}
		return false;
	}

	private string GetGroupLayerName(Group g)
	{
		var live = g.renderers.FirstOrDefault(r => r != null);
		if (live == null)
		{
			return "?";
		}
		// Mixed layers inside one group are possible; show the first + marker.
		int layer = live.gameObject.layer;
		bool mixed = g.renderers.Any(r => r != null && r.gameObject.layer != layer);
		return LayerMask.LayerToName(layer) + (mixed ? "*" : "");
	}

	private void MoveCheckedToLayer()
	{
		int moved = 0;
		foreach (var g in m_groups.Where(g => g.selected))
		{
			foreach (var r in g.renderers)
			{
				if (r == null)
				{
					continue;
				}
				Undo.RecordObject(r.gameObject, "RTAS layer move");
				r.gameObject.layer = m_targetLayer;
				moved++;
			}
		}
		Debug.Log($"[RtasAudit] Moved {moved} objects to layer '{LayerMask.LayerToName(m_targetLayer)}'. ");
	}
}
