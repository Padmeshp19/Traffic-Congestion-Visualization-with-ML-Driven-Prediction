using System.Collections.Generic;
using UnityEngine;

public class FlyoverBikeSimulation : MonoBehaviour
{
    [Header("References")]
    public TrafficAPIManager trafficAPI;
    [Tooltip("Counts every lap a flyover bike finishes, worth 600 x lap time / hour length. Found automatically if empty.")]
    public JunctionSpawner counter;
    public Transform[] routePoints;
    public GameObject[] bikePrefabs;

    [Header("Simulation Settings")]
    [Min(1f)] public float vehiclesPerBike = 600f;
    [Range(1, 20)] public int maxFlyoverBikes = 10;
    [Min(0.5f)] public float bikeSpeed = 3f;
    [Min(0.5f)] public float spacing = 2f;

    private readonly List<GameObject> spawnedBikes =
        new List<GameObject>();

    private void Awake()
    {
        if (trafficAPI == null)
            trafficAPI = FindFirstObjectByType<TrafficAPIManager>();

        if (counter == null)
            counter = FindFirstObjectByType<JunctionSpawner>();
    }

    private void Update()
    {
        if (trafficAPI == null ||
            routePoints == null || routePoints.Length < 2 ||
            bikePrefabs == null || bikePrefabs.Length == 0)
            return;

        int targetCount = 0;

        if (trafficAPI.FlyoverOn)
        {
            targetCount = Mathf.Clamp(
                Mathf.CeilToInt(
                    trafficAPI.FlyoverMotorcycles /
                    Mathf.Max(1f, vehiclesPerBike)
                ),
                0,
                maxFlyoverBikes
            );
        }

        while (spawnedBikes.Count < targetCount)
        {
            int previousCount = spawnedBikes.Count;
            SpawnBike(previousCount);

            if (spawnedBikes.Count == previousCount)
                break;
        }

        while (spawnedBikes.Count > targetCount)
        {
            int last = spawnedBikes.Count - 1;

            if (spawnedBikes[last] != null)
                Destroy(spawnedBikes[last]);

            spawnedBikes.RemoveAt(last);
        }
    }

    private void SpawnBike(int index)
    {
        GameObject prefab =
            bikePrefabs[index % bikePrefabs.Length];

        if (prefab == null)
            return;

        GameObject bike = Instantiate(prefab);

        LaneCarController laneController =
            bike.GetComponent<LaneCarController>();

        if (laneController != null)
            laneController.enabled = false;

        FlyoverBikeMover mover = bike.GetComponent<FlyoverBikeMover>();

        if (mover == null)
            mover = bike.AddComponent<FlyoverBikeMover>();

        mover.Initialize(
            routePoints,
            bikeSpeed,
            index * spacing
        );

        mover.LapCompleted += () =>
        {
            // Counted the same way as the ground road: one lap is worth 600 x lap time / hour length.
            if (counter != null)
                counter.RegisterFlyoverLap(mover.LastLapSeconds, mover.FullLapSeconds, vehiclesPerBike);
        };

        bike.name = "FlyoverBike_" + (index + 1);
        spawnedBikes.Add(bike);
    }

    private void OnDisable()
    {
        foreach (GameObject bike in spawnedBikes)
        {
            if (bike != null)
                Destroy(bike);
        }

        spawnedBikes.Clear();
    }
}