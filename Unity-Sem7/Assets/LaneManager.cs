using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// One lane of the junction. Keeps track of the vehicles driving in it so they never overlap.
///
/// Distances are measured along each vehicle's own driving direction (its blue Z arrow) and use
/// the vehicle lengths from VehicleInfo, so this works for lanes pointing in any direction.
/// (The old version compared world Z positions, which did not work for lanes running along X.)
/// </summary>
public class LaneManager : MonoBehaviour
{
    [Tooltip("Vehicles further than this to the side are treated as being in a different lane.")]
    [SerializeField] private float lateralTolerance = 0.5f;

    private readonly List<LaneCarController> cars = new List<LaneCarController>();

    /// <summary>Number of vehicles currently in this lane.</summary>
    public int Count
    {
        get
        {
            Cleanup();
            return cars.Count;
        }
    }

    public IReadOnlyList<LaneCarController> Vehicles
    {
        get
        {
            Cleanup();
            return cars;
        }
    }

    public void Register(LaneCarController car)
    {
        if (car != null && !cars.Contains(car))
            cars.Add(car);
    }

    public void Unregister(LaneCarController car)
    {
        cars.Remove(car);
    }

    /// <summary>
    /// Free road between this vehicle's front bumper and the back bumper of the nearest vehicle
    /// ahead of it in this lane. Infinity if the road ahead is empty.
    /// </summary>
    public float FreeSpaceAhead(LaneCarController car)
    {
        Cleanup();

        Vector3 position = car.transform.position;
        Vector3 forward = Flat(car.transform.forward);
        float nearest = float.PositiveInfinity;

        foreach (LaneCarController other in cars)
        {
            if (other == car)
                continue;

            Vector3 offset = other.transform.position - position;
            float along = Vector3.Dot(offset, forward);

            if (along <= 0f)
                continue;   // behind this vehicle

            if (Sideways(offset, forward, along) > lateralTolerance)
                continue;   // beside it, in another lane

            float free = along - car.HalfLength - other.HalfLength;
            if (free < nearest)
                nearest = free;
        }

        return nearest;
    }

    /// <summary>Kept for older scripts: true if the vehicle has more than <paramref name="gap"/> of free road ahead.</summary>
    public bool CanMove(LaneCarController car, float gap)
    {
        return FreeSpaceAhead(car) > gap;
    }

    /// <summary>
    /// For the spawner: can a vehicle with this half-length and safety gap be placed at
    /// <paramref name="center"/> without overlapping anyone, keeping everyone's safety gaps?
    /// </summary>
    public bool IsClear(Vector3 center, Vector3 forward, float halfLength, float safetyGap)
    {
        Cleanup();
        forward = Flat(forward);

        foreach (LaneCarController other in cars)
        {
            Vector3 offset = other.transform.position - center;
            float along = Vector3.Dot(offset, forward);

            if (Sideways(offset, forward, along) > lateralTolerance)
                continue;

            float needed = along >= 0f
                ? halfLength + other.HalfLength + safetyGap          // other is ahead: keep the new vehicle's gap
                : halfLength + other.HalfLength + other.SafetyGap;   // other is behind: leave the other's gap

            if (Mathf.Abs(along) < needed)
                return false;
        }

        return true;
    }

    private static Vector3 Flat(Vector3 direction)
    {
        direction.y = 0f;
        return direction.sqrMagnitude > 0.000001f ? direction.normalized : Vector3.forward;
    }

    private static float Sideways(Vector3 offset, Vector3 forward, float along)
    {
        Vector3 side = offset - forward * along;
        side.y = 0f;
        return side.magnitude;
    }

    private void Cleanup()
    {
        cars.RemoveAll(car => car == null);
    }
}