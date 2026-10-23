using System;
using UnityEngine;

/// <summary>
/// Drives a vehicle along its lane:
///   1. leaves the road at the end point (removed, or parked if Remove At End is off),
///   2. keeps at least its safety gap behind the vehicle ahead,
///   3. on red, stops with its front bumper at the stop line - unless it has already passed it,
///   4. otherwise moves forward along its blue Z arrow.
///
/// Phase 2 rewrite. The Inspector field names are unchanged, so existing scene settings are kept.
/// Vehicles with a VehicleInfo use their real length and safety gap; older vehicles without one
/// keep "Min Gap" between vehicle centres, as before.
/// </summary>
public class LaneCarController : MonoBehaviour
{
    [Header("Movement")]
    public float speed = 2.5f;

    [Header("Lane (ANTI-COLLISION)")]
    public LaneManager lane;               // MUST be assigned (the spawner does this for spawned vehicles)
    [Tooltip("Only used by vehicles without a VehicleInfo: distance kept between vehicle centres.")]
    public float minGap = 2.5f;

    [Header("Signal")]
    public Transform stopPoint;
    [Tooltip("A red light is obeyed once the stop line is this close ahead. The vehicle stops with its front at the line.")]
    public float stopDistance = 3f;
    public TrafficSignalController signal;

    [Header("End Of Road")]
    public Transform endPoint;
    public float endStopDistance = 2f;
    [Tooltip("Remove the vehicle when it reaches the end point. Untick to make it park there instead.")]
    public bool removeAtEnd = true;

    /// <summary>Sent once when the vehicle reaches the end of the road (just before it is removed).</summary>
    public event Action<LaneCarController> ReachedEnd;

    public VehicleInfo Info { get; private set; }
    public float HalfLength => Info != null ? Info.HalfLength : 0f;
    public float SafetyGap => Info != null ? Info.safetyGap : minGap;
    public bool IsStopped { get; private set; }
    public bool HasFinished { get; private set; }

    // Small allowance so rounding errors never let a vehicle creep over the stop line on red.
    private const float StopLineTolerance = 0.02f;

    private void Awake()
    {
        Info = GetComponent<VehicleInfo>();
    }

    private void OnEnable()
    {
        if (lane != null && !HasFinished)
            lane.Register(this);
    }

    private void Start()
    {
        if (lane != null)
            lane.Register(this);
        else
            Debug.LogError($"{name} has NO LaneManager assigned!");
    }

    private void OnDisable()
    {
        if (lane != null)
            lane.Unregister(this);
    }

    /// <summary>Used by the spawner right after creating a vehicle. Registers it in the lane immediately.</summary>
    public void Setup(LaneManager newLane, Transform newStopPoint, TrafficSignalController newSignal,
                      Transform newEndPoint, float newSpeed)
    {
        stopPoint = newStopPoint;
        signal = newSignal;
        endPoint = newEndPoint;
        speed = newSpeed;
        SetLane(newLane);
    }

    public void SetLane(LaneManager newLane)
    {
        if (lane != null && lane != newLane)
            lane.Unregister(this);

        lane = newLane;

        if (lane != null && isActiveAndEnabled && !HasFinished)
            lane.Register(this);
    }

    private void Update()
    {
        if (HasFinished)
            return;

        // 1. End of road
        if (endPoint != null && DistanceAhead(endPoint.position) <= endStopDistance)
        {
            Finish();
            return;
        }

        float step = speed * Time.deltaTime;

        // 2. Car following: never get closer than the safety gap to the vehicle ahead
        if (lane != null)
            step = Mathf.Min(step, lane.FreeSpaceAhead(this) - SafetyGap);

        // 3. Red light: stop with the front bumper at the stop line, unless already past it
        if (signal != null && stopPoint != null && signal.IsRed())
        {
            float toLine = DistanceAhead(stopPoint.position) - HalfLength;
            if (toLine >= -StopLineTolerance && toLine <= stopDistance)
                step = Mathf.Min(step, Mathf.Max(0f, toLine));
        }

        // 4. Move
        IsStopped = step <= 0.0001f;
        if (!IsStopped)
            transform.Translate(Vector3.forward * step);
    }

    /// <summary>How far ahead of this vehicle's centre a point is, along its driving direction.</summary>
    private float DistanceAhead(Vector3 point)
    {
        Vector3 forward = transform.forward;
        forward.y = 0f;
        forward = forward.sqrMagnitude > 0.000001f ? forward.normalized : Vector3.forward;
        return Vector3.Dot(point - transform.position, forward);
    }

    private void Finish()
    {
        HasFinished = true;
        IsStopped = true;
        ReachedEnd?.Invoke(this);

        if (removeAtEnd)
            Destroy(gameObject);   // OnDisable takes it out of the lane
        // If parked, it stays in the lane so vehicles behind queue up behind it.
    }
}