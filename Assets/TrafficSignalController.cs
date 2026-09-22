using UnityEngine;

public class TrafficSignalController : MonoBehaviour
{
    [Header("Signal Lights (Drag the actual objects here)")]
    public GameObject redLight;
    public GameObject yellowLight;
    public GameObject greenLight;

    [Header("Timing (seconds)")]
    public float redTime = 6f;
    public float yellowTime = 2f;
    public float greenTime = 6f;

    private float timer = 0f;

    private enum SignalState
    {
        Red,
        Green,
        Yellow
    }

    private SignalState currentState;

    // 🔍 Cars will use this
    public bool IsRed()
    {
        return currentState == SignalState.Red;
    }

    public bool IsGreen()
    {
        return currentState == SignalState.Green;
    }

    void Start()
    {
        // 🔴 START WITH RED
        currentState = SignalState.Red;
        timer = 0f;
        UpdateLights();

        Debug.Log("Traffic Light STARTED → RED");
    }

    void Update()
    {
        timer += Time.deltaTime;

        switch (currentState)
        {
            case SignalState.Red:
                if (timer >= redTime)
                    SwitchTo(SignalState.Green);
                break;

            case SignalState.Green:
                if (timer >= greenTime)
                    SwitchTo(SignalState.Yellow);
                break;

            case SignalState.Yellow:
                if (timer >= yellowTime)
                    SwitchTo(SignalState.Red);
                break;
        }
    }

    void SwitchTo(SignalState next)
    {
        currentState = next;
        timer = 0f;
        UpdateLights();

        Debug.Log("Traffic Light → " + currentState);
    }

    void UpdateLights()
    {
        if (redLight) redLight.SetActive(currentState == SignalState.Red);
        if (yellowLight) yellowLight.SetActive(currentState == SignalState.Yellow);
        if (greenLight) greenLight.SetActive(currentState == SignalState.Green);
    }
}
