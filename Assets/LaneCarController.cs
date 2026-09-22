using UnityEngine;

public class LaneCarController : MonoBehaviour
{
    [Header("Movement")]
    public float speed = 2.5f;

    [Header("Lane (ANTI-COLLISION)")]
    public LaneManager lane;               // MUST be assigned
    public float minGap = 2.5f;             // spacing between cars

    [Header("Signal")]
    public Transform stopPoint;
    public float stopDistance = 3f;
    public TrafficSignalController signal;

    [Header("End Of Road")]
    public Transform endPoint;
    public float endStopDistance = 2f;

    private bool stoppedAtEnd = false;

    void Start()
    {
        if (lane != null)
            lane.Register(this);
        else
            Debug.LogError($"{name} has NO LaneManager assigned!");
    }

    void OnDestroy()
    {
        if (lane != null)
            lane.Unregister(this);
    }

    void Update()
    {
        if (stoppedAtEnd) return;

        // 1️⃣ CAR FOLLOWING (ANTI-COLLISION)
        if (lane != null && !lane.CanMove(this, minGap))
            return;

        // 2️⃣ RED SIGNAL STOP
        if (signal != null && stopPoint != null && signal.IsRed())
        {
            float d = Vector3.Distance(transform.position, stopPoint.position);
            if (d <= stopDistance)
                return;
        }

        // 3️⃣ END OF ROAD STOP
        if (endPoint != null)
        {
            float d = Vector3.Distance(transform.position, endPoint.position);
            if (d <= endStopDistance)
            {
                stoppedAtEnd = true;
                return;
            }
        }

        // 4️⃣ MOVE
        transform.Translate(Vector3.forward * speed * Time.deltaTime);
    }
}
