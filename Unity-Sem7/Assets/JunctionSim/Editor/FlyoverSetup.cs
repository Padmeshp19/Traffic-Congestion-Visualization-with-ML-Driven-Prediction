using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Editor tool: Tools > Junction Sim > Set Up Flyover Toggle
///
/// Finds the pieces of the 2-wheeler flyover (objects named Flyover... or ...piller/pillar that sit
/// along the kerb side of the road, z 2.4 to 3.4, below 2.5 high) and puts them in a FlyoverVisibility
/// component on a "FlyoverToggle" object. The tall structure along the median (also called "Flyover",
/// about 3 high at z 0) and its pillars are left out. Safe to run again. Undo (Ctrl+Z) reverts it.
/// </summary>
public static class FlyoverSetup
{
    // Where the 2-wheeler flyover is (measured from Traffic junction.unity).
    private const float MinZ = 2.4f;
    private const float MaxZ = 3.4f;
    private const float MaxTop = 2.5f;

    [MenuItem("Tools/Junction Sim/Set Up Flyover Toggle")]
    public static void SetUp()
    {
        var parts = new List<GameObject>();
        foreach (GameObject root in SceneManager.GetActiveScene().GetRootGameObjects())
            Collect(root.transform, parts);

        // Exclude our own helper object if it exists.
        parts.RemoveAll(p => p.GetComponent<FlyoverVisibility>() != null);

        GameObject holder = GameObject.Find("FlyoverToggle");
        if (holder == null)
        {
            holder = new GameObject("FlyoverToggle");
            Undo.RegisterCreatedObjectUndo(holder, "Create FlyoverToggle");
        }

        FlyoverVisibility toggle = holder.GetComponent<FlyoverVisibility>();
        if (toggle == null)
            toggle = Undo.AddComponent<FlyoverVisibility>(holder);

        Undo.RecordObject(toggle, "Set up flyover toggle");
        toggle.flyoverParts = parts.ToArray();
        toggle.trafficAPI = Object.FindFirstObjectByType<TrafficAPIManager>();
        EditorUtility.SetDirty(toggle);
        EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());

        var report = new StringBuilder($"FlyoverSetup: {parts.Count} flyover part(s) will be hidden when the flyover is OFF:\n");
        foreach (GameObject part in parts.OrderBy(p => p.transform.position.x))
            report.AppendLine($"  {part.name}   (x {part.transform.position.x:0.0})");
        if (toggle.trafficAPI == null)
            report.AppendLine("WARNING: no TrafficAPIManager in the scene. Add it, then run this again.");
        Debug.Log(report.ToString());

        EditorUtility.DisplayDialog("Flyover toggle",
            $"Found {parts.Count} flyover parts (listed in the Console).\n\n" +
            "In Play mode they are hidden while the flyover is OFF and rise into place when it is switched ON. " +
            "Save the scene to keep this.", "OK");
    }

    /// <summary>Adds this object if it is a flyover piece; otherwise looks inside it.</summary>
    private static void Collect(Transform t, List<GameObject> parts)
    {
        if (IsFlyoverPiece(t.gameObject))
        {
            parts.Add(t.gameObject);   // its children move and hide with it
            return;
        }

        foreach (Transform child in t)
            Collect(child, parts);
    }

    private static bool IsFlyoverPiece(GameObject go)
    {
        string n = go.name.ToLowerInvariant();
        if (!n.Contains("flyover") && !n.Contains("piller") && !n.Contains("pillar"))
            return false;

        Renderer[] renderers = go.GetComponentsInChildren<Renderer>(true);
        if (renderers.Length == 0)
            return false;

        Bounds box = renderers[0].bounds;
        foreach (Renderer r in renderers)
            box.Encapsulate(r.bounds);

        return box.center.z >= MinZ && box.center.z <= MaxZ && box.max.y <= MaxTop;
    }
}