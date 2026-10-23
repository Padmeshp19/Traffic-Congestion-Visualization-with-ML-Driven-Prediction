using System;
using UnityEngine;

/// <summary>
/// Shows the 2-wheeler flyover only when the flyover is ON.
///
///   Flyover OFF  -> the flyover parts are hidden (Play mode only; in Edit mode they stay visible).
///   Flyover ON   -> the parts rise out of the ground in a wave along the flyover, then stay in place.
///   OFF again    -> they sink back into the ground and are hidden.
///
/// ON/OFF comes from TrafficAPIManager (F key or the Flyover tick box on the TrafficAPI object).
/// The list of parts is filled by Tools > Junction Sim > Set Up Flyover Toggle.
///
/// For the flyover bike simulation: IsBuilt is true once the flyover is fully in place, and
/// VisibilityChanged fires when it starts appearing (true) or disappearing (false).
/// </summary>
public class FlyoverVisibility : MonoBehaviour
{
    [Tooltip("Leave empty to find it in the scene.")]
    public TrafficAPIManager trafficAPI;

    [Tooltip("The 2-wheeler flyover pieces: ramps, deck, side barricades, short pillars.")]
    public GameObject[] flyoverParts;

    [Header("Animation")]
    [Tooltip("Rise out of the ground when switched ON and sink when switched OFF. Untick to just appear/disappear.")]
    public bool animate = true;
    [Tooltip("Seconds each piece takes to rise.")]
    public float riseSeconds = 1.2f;
    [Tooltip("Extra seconds from the first piece to the last, so the flyover builds up along its length.")]
    public float waveSeconds = 0.8f;
    [Tooltip("How far below the ground each piece starts.")]
    public float riseDepth = 2.2f;

    /// <summary>True when the flyover is switched ON (it may still be rising).</summary>
    public bool IsShown { get; private set; }

    /// <summary>True once every piece has finished rising and the flyover is fully in place.</summary>
    public bool IsBuilt => IsShown && timeline >= TotalSeconds - 0.0001f;

    /// <summary>Fires with true when the flyover starts appearing and false when it starts disappearing.</summary>
    public event Action<bool> VisibilityChanged;

    private GameObject[] parts;        // copy of flyoverParts taken when Play starts
    private Vector3[] homePositions;
    private float[] delays;
    private float timeline;            // 0 = fully hidden, TotalSeconds = fully built
    private bool ready;

    private float TotalSeconds => Mathf.Max(0.01f, riseSeconds) + Mathf.Max(0f, waveSeconds);

    // ==========================================================================================

    private void OnEnable()
    {
        if (trafficAPI == null)
            trafficAPI = FindFirstObjectByType<TrafficAPIManager>();

        if (trafficAPI == null)
        {
            Debug.LogWarning("FlyoverVisibility: no TrafficAPIManager in the scene, so the flyover stays visible.");
            return;
        }

        RememberHomePositions();
        trafficAPI.FlyoverChanged += OnFlyoverChanged;

        // Start in the current state straight away, without animating.
        IsShown = trafficAPI.FlyoverOn;
        timeline = IsShown ? TotalSeconds : 0f;
        ApplyTimeline();
    }

    private void OnDisable()
    {
        if (trafficAPI != null)
            trafficAPI.FlyoverChanged -= OnFlyoverChanged;

        // Put every piece back exactly where it was, visible.
        if (ready)
        {
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i] == null)
                    continue;
                parts[i].transform.position = homePositions[i];
                parts[i].SetActive(true);
            }
        }
    }

    private void Update()
    {
        if (!ready || trafficAPI == null)
            return;

        // Safety net in case a change was missed.
        if (trafficAPI.FlyoverOn != IsShown)
            OnFlyoverChanged(trafficAPI.FlyoverOn);

        float goal = IsShown ? TotalSeconds : 0f;
        if (timeline == goal)
            return;   // finished; MoveTowards always lands exactly on the goal

        timeline = animate ? Mathf.MoveTowards(timeline, goal, Time.deltaTime) : goal;
        ApplyTimeline();
    }

    private void OnFlyoverChanged(bool isOn)
    {
        if (isOn == IsShown)
            return;

        IsShown = isOn;
        VisibilityChanged?.Invoke(isOn);
    }

    // ==========================================================================================

    private void RememberHomePositions()
    {
        if (ready)
            return;

        parts = (GameObject[])(flyoverParts ?? new GameObject[0]).Clone();
        homePositions = new Vector3[parts.Length];
        delays = new float[parts.Length];

        // Pieces rise in a wave from one end of the flyover to the other.
        float minX = float.MaxValue, maxX = float.MinValue;
        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i] == null)
                continue;
            homePositions[i] = parts[i].transform.position;
            minX = Mathf.Min(minX, homePositions[i].x);
            maxX = Mathf.Max(maxX, homePositions[i].x);
        }

        for (int i = 0; i < parts.Length; i++)
        {
            float along = maxX > minX ? (homePositions[i].x - minX) / (maxX - minX) : 0f;
            delays[i] = along * Mathf.Max(0f, waveSeconds);
        }

        ready = true;
    }

    /// <summary>Moves and shows/hides every piece for the current point of the animation.</summary>
    private void ApplyTimeline()
    {
        if (!ready)
            return;

        float rise = Mathf.Max(0.01f, riseSeconds);
        for (int i = 0; i < parts.Length; i++)
        {
            GameObject part = parts[i];
            if (part == null)
                continue;

            float progress = Mathf.Clamp01((timeline - delays[i]) / rise);
            float eased = 1f - (1f - progress) * (1f - progress) * (1f - progress);   // fast start, gentle landing

            bool visible = progress > 0.0001f;
            if (part.activeSelf != visible)
                part.SetActive(visible);

            part.transform.position = homePositions[i] - Vector3.up * (riseDepth * (1f - eased));
        }
    }
}