using UnityEngine;

/// <summary>
/// Describes one lane of the junction for the spawner: where vehicles enter (Start), where they
/// stop on red (Stop Line), where they leave (End), which vehicle types may use it, and the
/// crossing area where vehicles must never be spawned.
///
/// Lives on RoadLane_1/2/3 next to their LaneManager. Set up automatically by
/// Tools > Junction Sim > Set Up Lanes. The lane is drawn in the Scene view as a coloured strip.
/// </summary>
[RequireComponent(typeof(LaneManager))]
public class JunctionLane : MonoBehaviour
{
    [Header("Markers (vehicles drive from Start towards End)")]
    public Transform startPoint;
    public Transform stopLine;
    public Transform endPoint;
    public TrafficSignalController signal;

    [Header("Size")]
    [Tooltip("Width of the painted lane, only used for drawing and checks.")]
    public float laneWidth = 0.8f;
    [Tooltip("Length of the crossing after the stop line. Vehicles are never spawned inside it.")]
    public float crossingLength = 2.3f;
    [Tooltip("Vehicles are never spawned this close to the end point.")]
    public float endMargin = 3f;

    [Header("Which vehicles may use this lane")]
    public bool allowCars = true;
    public bool allowBikes = true;
    public bool allowBuses = true;
    public bool allowTrucks = true;
    [Tooltip("Buses and trucks pick this lane first when it has room (Indian traffic: heavy vehicles keep left).")]
    public bool heavyVehiclesPreferred;

    private LaneManager laneManager;

    public LaneManager Lane
    {
        get
        {
            if (laneManager == null)
                laneManager = GetComponent<LaneManager>();
            return laneManager;
        }
    }

    public bool IsValid => startPoint != null && stopLine != null && endPoint != null && Lane != null;

    /// <summary>Driving direction, flat (no up/down).</summary>
    public Vector3 Direction
    {
        get
        {
            Vector3 d = endPoint.position - startPoint.position;
            d.y = 0f;
            return d.sqrMagnitude > 0.000001f ? d.normalized : Vector3.forward;
        }
    }

    public Quaternion Rotation => Quaternion.LookRotation(Direction, Vector3.up);

    /// <summary>Distance from Start to End along the lane.</summary>
    public float Length => DistanceAlong(endPoint.position);

    /// <summary>Distance from Start to the stop line along the lane.</summary>
    public float StopLineDistance => DistanceAlong(stopLine.position);

    public bool Allows(VehicleType type)
    {
        switch (type)
        {
            case VehicleType.Car: return allowCars;
            case VehicleType.Bike: return allowBikes;
            case VehicleType.Bus: return allowBuses;
            case VehicleType.Truck: return allowTrucks;
            default: return false;
        }
    }

    /// <summary>How far along the lane (from Start) a world position is.</summary>
    public float DistanceAlong(Vector3 worldPosition)
    {
        return Vector3.Dot(worldPosition - startPoint.position, Direction);
    }

    /// <summary>World position on the lane centre line, <paramref name="distance"/> from Start. Height follows the markers.</summary>
    public Vector3 PointAt(float distance)
    {
        float length = Length;
        float t = length > 0.0001f ? Mathf.Clamp01(distance / length) : 0f;
        Vector3 point = startPoint.position + Direction * distance;
        point.y = Mathf.Lerp(startPoint.position.y, endPoint.position.y, t);
        return point;
    }

    /// <summary>
    /// True if a vehicle centred at <paramref name="distance"/> with this half-length would stick out
    /// of the road, touch the crossing, or sit too close to the end.
    /// </summary>
    public bool IsNoSpawn(float distance, float halfLength)
    {
        float back = distance - halfLength;
        float front = distance + halfLength;

        if (back < 0f || front > Length - endMargin)
            return true;   // off the start of the road, or too close to the end

        float crossingStart = StopLineDistance;
        float crossingEnd = crossingStart + crossingLength;
        return front > crossingStart && back < crossingEnd;   // overlaps the stop line / crossing
    }

    private void OnDrawGizmos()
    {
        if (startPoint == null || stopLine == null || endPoint == null)
            return;

        Color laneColour = allowCars || allowBuses || allowTrucks
            ? new Color(0.2f, 0.9f, 1f)    // mixed traffic: cyan
            : new Color(1f, 0.4f, 1f);     // bikes only: pink

        float length = Length;
        float stop = StopLineDistance;

        // Lane strip from Start to End
        Gizmos.matrix = Matrix4x4.TRS(startPoint.position, Rotation, Vector3.one);
        Gizmos.color = laneColour;
        Gizmos.DrawWireCube(new Vector3(0f, 0.02f, length * 0.5f), new Vector3(laneWidth, 0.02f, length));

        // Crossing (no-spawn) zone in orange, stop line in red
        Gizmos.color = new Color(1f, 0.6f, 0f);
        Gizmos.DrawWireCube(new Vector3(0f, 0.03f, stop + crossingLength * 0.5f), new Vector3(laneWidth, 0.04f, crossingLength));
        Gizmos.color = Color.red;
        Gizmos.DrawCube(new Vector3(0f, 0.03f, stop), new Vector3(laneWidth, 0.04f, 0.06f));

        // Direction arrows
        Gizmos.color = laneColour;
        for (float d = 2f; d < length - 1f; d += 6f)
        {
            Gizmos.DrawLine(new Vector3(0f, 0.05f, d - 0.4f), new Vector3(0f, 0.05f, d + 0.4f));
            Gizmos.DrawLine(new Vector3(-0.15f, 0.05f, d + 0.2f), new Vector3(0f, 0.05f, d + 0.4f));
            Gizmos.DrawLine(new Vector3(0.15f, 0.05f, d + 0.2f), new Vector3(0f, 0.05f, d + 0.4f));
        }

        // Start (green) and End (blue)
        Gizmos.matrix = Matrix4x4.identity;
        Gizmos.color = Color.green;
        Gizmos.DrawSphere(startPoint.position, 0.12f);
        Gizmos.color = Color.blue;
        Gizmos.DrawSphere(endPoint.position, 0.12f);
    }
}