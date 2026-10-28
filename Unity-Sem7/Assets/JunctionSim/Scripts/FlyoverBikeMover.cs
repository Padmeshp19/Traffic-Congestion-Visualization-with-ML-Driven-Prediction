using UnityEngine;

public class FlyoverBikeMover : MonoBehaviour
{
    /// <summary>Sent each time the bike completes a lap of the flyover.</summary>
    public event System.Action LapCompleted;

    /// <summary>Real seconds the lap that just finished took. 0 after the first lap, which starts part-way round.</summary>
    public float LastLapSeconds { get; private set; }

    /// <summary>Seconds a whole lap takes at this speed (route length / speed).</summary>
    public float FullLapSeconds => speed > 0.001f ? totalLength / speed : 0f;

    private Transform[] points;
    private float speed;
    private float distanceTravelled;
    private float totalLength;
    private float lapStartTime = -1f;   // -1 = the current lap did not start at the beginning of the route

    public void Initialize(
        Transform[] routePoints,
        float movementSpeed,
        float startingDistance)
    {
        points = routePoints;
        speed = movementSpeed;
        distanceTravelled = startingDistance;
        lapStartTime = -1f;
        LastLapSeconds = 0f;

        CalculateRouteLength();

        if (totalLength > 0f)
        {
            distanceTravelled =
                Mathf.Repeat(distanceTravelled, totalLength);

            UpdatePosition();
        }
    }

    private void CalculateRouteLength()
    {
        totalLength = 0f;

        if (points == null || points.Length < 2)
            return;

        for (int i = 0; i < points.Length - 1; i++)
        {
            if (points[i] == null || points[i + 1] == null)
                continue;

            totalLength += Vector3.Distance(
                points[i].position,
                points[i + 1].position
            );
        }
    }

    private void Update()
    {
        if (points == null || points.Length < 2 ||
            totalLength <= 0.001f)
            return;

        distanceTravelled += speed * Time.deltaTime;

        if (distanceTravelled >= totalLength)
        {
            LastLapSeconds = lapStartTime >= 0f ? Time.time - lapStartTime : 0f;
            lapStartTime = Time.time;
            LapCompleted?.Invoke();
        }

        distanceTravelled =
            Mathf.Repeat(distanceTravelled, totalLength);

        UpdatePosition();
    }

    private void UpdatePosition()
    {
        float remaining = distanceTravelled;

        for (int i = 0; i < points.Length - 1; i++)
        {
            if (points[i] == null || points[i + 1] == null)
                continue;

            Vector3 start = points[i].position;
            Vector3 end = points[i + 1].position;

            float segmentLength = Vector3.Distance(start, end);

            if (segmentLength <= 0.001f)
                continue;

            if (remaining <= segmentLength)
            {
                float t = remaining / segmentLength;

                transform.position = Vector3.Lerp(start, end, t);

                Vector3 direction = (end - start).normalized;

                if (direction.sqrMagnitude > 0.001f)
                {
                    transform.rotation =
                        Quaternion.LookRotation(direction, Vector3.up);
                }

                return;
            }

            remaining -= segmentLength;
        }
    }
}