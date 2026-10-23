using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Editor tool (Phase 3). Tools > Junction Sim > Set Up Lanes
///
/// Adds a JunctionLane to RoadLane_1/2/3 and gives each lane three new markers (JS_Start,
/// JS_StopLine, JS_End) in the middle of the painted lanes. The old Point_Junction / EndPoint
/// markers are left untouched. Safe to run again: it moves the same markers instead of adding new ones.
///
/// Positions were measured from Traffic junction.unity:
///   main road surface        x -25.46 .. 30.41
///   crossing (Road_Vertical) x  -1.26 .. 0.30
///   painted lines (our side) median edge z 0.50, dashed lines z 1.45 and 2.29, solid edge z 2.68
/// Undo (Ctrl+Z) reverts everything the tool did.
/// </summary>
public static class JunctionLaneSetup
{
    private const float StartX = -24.5f;      // 1 unit inside the start of the road
    private const float StopLineX = -1.96f;   // same x as the old RoadLane_2 stop marker, just before the crossing
    private const float CrossingFarX = 0.30f; // far edge of the crossing road
    private const float EndX = 29.5f;         // 1 unit before the end of the road
    private const float FallbackRoadHeight = 0.05f;

    private class LaneSpec
    {
        public string Name;
        public float CentreZ;
        public float Width;
        public bool Cars, Bikes, Buses, Trucks;
        public bool HeavyPreferred;
    }

    // ---- Change CentreZ / Width here if a lane does not sit between the painted lines. ----
    private static readonly LaneSpec[] Lanes =
    {
        new LaneSpec { Name = "RoadLane_1", CentreZ = 0.95f,  Width = 0.90f, Cars = true, Bikes = true, Buses = true, Trucks = true },
        new LaneSpec { Name = "RoadLane_2", CentreZ = 1.87f,  Width = 0.80f, Cars = true, Bikes = true, Buses = true, Trucks = true, HeavyPreferred = true },
        new LaneSpec { Name = "RoadLane_3", CentreZ = 2.485f, Width = 0.38f, Bikes = true },
    };

    [MenuItem("Tools/Junction Sim/Set Up Lanes")]
    public static void SetUpLanes()
    {
        var report = new StringBuilder("JunctionLaneSetup:\n");
        var signal = Object.FindFirstObjectByType<TrafficSignalController>();
        if (signal == null)
            report.AppendLine("WARNING: no TrafficSignalController found in the scene; lanes will ignore red lights.");

        int done = 0;
        foreach (LaneSpec spec in Lanes)
        {
            GameObject laneObject = GameObject.Find(spec.Name);
            if (laneObject == null)
            {
                report.AppendLine($"SKIPPED {spec.Name}: not found in the open scene.");
                continue;
            }

            if (laneObject.GetComponent<LaneManager>() == null)
                Undo.AddComponent<LaneManager>(laneObject);

            JunctionLane lane = laneObject.GetComponent<JunctionLane>();
            if (lane == null)
                lane = Undo.AddComponent<JunctionLane>(laneObject);

            Undo.RecordObject(lane, "Set up junction lane");
            lane.startPoint = Marker(laneObject.transform, "JS_Start", new Vector3(StartX, 0f, spec.CentreZ));
            lane.stopLine = Marker(laneObject.transform, "JS_StopLine", new Vector3(StopLineX, 0f, spec.CentreZ));
            lane.endPoint = Marker(laneObject.transform, "JS_End", new Vector3(EndX, 0f, spec.CentreZ));
            lane.signal = signal;
            lane.laneWidth = spec.Width;
            lane.crossingLength = CrossingFarX - StopLineX;
            lane.allowCars = spec.Cars;
            lane.allowBikes = spec.Bikes;
            lane.allowBuses = spec.Buses;
            lane.allowTrucks = spec.Trucks;
            lane.heavyVehiclesPreferred = spec.HeavyPreferred;
            EditorUtility.SetDirty(lane);

            report.AppendLine($"{spec.Name}: centre z {spec.CentreZ}, width {spec.Width}, length {lane.Length:0.0}, " +
                              $"stop line at {lane.StopLineDistance:0.0} from the start, allows " +
                              $"{(spec.Cars ? "cars " : "")}{(spec.Bikes ? "bikes " : "")}{(spec.Buses ? "buses " : "")}{(spec.Trucks ? "trucks " : "")}" +
                              (spec.HeavyPreferred ? "(heavy vehicles prefer it)" : ""));
            done++;
        }

        EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
        report.Append($"Set up {done} of {Lanes.Length} lanes. Save the scene (Ctrl+S) to keep them.");
        Debug.Log(report.ToString());
        EditorUtility.DisplayDialog("Junction lanes",
            $"Set up {done} of {Lanes.Length} lanes.\n\nCheck the coloured strips in the Scene view, then save the scene.", "OK");
    }

    /// <summary>Finds or creates a child marker and puts it on the road surface at (x, z).</summary>
    private static Transform Marker(Transform laneObject, string name, Vector3 worldXZ)
    {
        Transform marker = laneObject.Find(name);
        if (marker == null)
        {
            var go = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(go, "Create lane marker");
            Undo.SetTransformParent(go.transform, laneObject, "Create lane marker");
            marker = go.transform;
        }

        Undo.RecordObject(marker, "Move lane marker");
        Vector3 position = worldXZ;
        position.y = RoadHeight(worldXZ);
        marker.position = position;
        marker.rotation = Quaternion.LookRotation(Vector3.right, Vector3.up);
        return marker;
    }

    private static float RoadHeight(Vector3 point)
    {
        // Start the ray below the flyover deck (y ~2.9) so it lands on the road surface.
        Vector3 origin = new Vector3(point.x, 1.5f, point.z);
        return Physics.Raycast(origin, Vector3.down, out RaycastHit hit, 3f) ? hit.point.y : FallbackRoadHeight;
    }
}