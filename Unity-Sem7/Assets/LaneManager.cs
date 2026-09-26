using System.Collections.Generic;
using UnityEngine;

public class LaneManager : MonoBehaviour
{
    private List<LaneCarController> cars = new List<LaneCarController>();

    public void Register(LaneCarController car)
    {
        if (!cars.Contains(car))
            cars.Add(car);
    }

    public void Unregister(LaneCarController car)
    {
        cars.Remove(car);
    }

    public bool CanMove(LaneCarController car, float gap)
    {
        foreach (var other in cars)
        {
            if (other == car) continue;

            // Only check cars ahead
            if (other.transform.position.z > car.transform.position.z)
            {
                float dist = other.transform.position.z - car.transform.position.z;
                if (dist < gap)
                    return false;
            }
        }
        return true;
    }
}
