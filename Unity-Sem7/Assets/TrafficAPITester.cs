using System;
using UnityEngine;

/// <summary>
/// End-to-end test tool for the Flask -> Unity connection.
///
/// It listens to TrafficAPIManager exactly like the spawner will, and:
///   - checks the motorcycle rules on every update
///       ground + flyover = predicted motorcycles
///       flyover OFF -> 0 bikes on the flyover
///       flyover ON  -> floor(motorcycles x 30%) on the flyover (same rule as Flask and the dashboard)
///   - records a before/after flyover comparison for the same road, day, hour and model
///   - shows a small status box in the top-right corner of the Game view (press T to hide/show).
///
/// It does not spawn or move anything, so it is safe to keep in the scene.
/// Untick the component to switch it off completely.
/// </summary>
public class TrafficAPITester : MonoBehaviour
{
    [Tooltip("Leave empty to find the TrafficAPIManager in the scene automatically.")]
    [SerializeField] private TrafficAPIManager trafficAPI;
    [SerializeField] private bool showOverlay = true;
    [SerializeField] private KeyCode toggleOverlayKey = KeyCode.T;
    [Tooltip("Write a one-line PASS summary to the Console on every update (FAIL lines are always written).")]
    [SerializeField] private bool logPassedChecks = true;

    public int PredictionsReceived { get; private set; }
    public int RoutingUpdates { get; private set; }
    public int RequestFailures { get; private set; }
    public int ChecksPassed { get; private set; }
    public int ChecksFailed { get; private set; }

    private struct Snapshot
    {
        public bool valid;
        public string query;
        public int totalBikes;
        public int ground;
        public int flyover;
    }

    private Snapshot flyoverOffResult;
    private Snapshot flyoverOnResult;
    private string comparisonText = "Not run yet. Press F to switch the flyover ON, then F again.";
    private string lastLoggedComparison;
    private string lastCheckSummary = "No checks yet.";
    private bool subscribed;

    private GUIStyle boxStyle;
    private GUIStyle textStyle;

    // ==========================================================================================
    // Subscribe / unsubscribe
    // ==========================================================================================

    private void OnEnable()
    {
        if (trafficAPI == null)
            trafficAPI = FindFirstObjectByType<TrafficAPIManager>();

        if (trafficAPI == null)
        {
            Debug.LogError("TrafficAPITester: no TrafficAPIManager found in the scene. " +
                           "Add TrafficAPIManager to a GameObject first.");
            return;
        }

        trafficAPI.PredictionUpdated += OnPredictionUpdated;
        trafficAPI.MotorcycleRoutingUpdated += OnMotorcycleRoutingUpdated;
        trafficAPI.RequestFailed += OnRequestFailed;
        trafficAPI.FlyoverChanged += OnFlyoverChanged;
        subscribed = true;
    }

    private void OnDisable()
    {
        if (!subscribed || trafficAPI == null)
            return;

        trafficAPI.PredictionUpdated -= OnPredictionUpdated;
        trafficAPI.MotorcycleRoutingUpdated -= OnMotorcycleRoutingUpdated;
        trafficAPI.RequestFailed -= OnRequestFailed;
        trafficAPI.FlyoverChanged -= OnFlyoverChanged;
        subscribed = false;
    }

    private void Update()
    {
#if ENABLE_LEGACY_INPUT_MANAGER
        if (Input.GetKeyDown(toggleOverlayKey))
            showOverlay = !showOverlay;
#endif
    }

    // ==========================================================================================
    // Event handlers
    // ==========================================================================================

    private void OnPredictionUpdated(TrafficAPIManager.TrafficPrediction prediction)
    {
        PredictionsReceived++;

        bool countsValid = prediction.cars >= 0 && prediction.motorcycles >= 0 &&
                           prediction.buses >= 0 && prediction.trucks >= 0;
        bool totalValid = prediction.total ==
                          prediction.cars + prediction.motorcycles + prediction.buses + prediction.trucks;

        Check(countsValid, "No negative vehicle counts",
              $"cars={prediction.cars}, motorcycles={prediction.motorcycles}, " +
              $"buses={prediction.buses}, trucks={prediction.trucks}");
        Check(totalValid, "Total equals cars + motorcycles + buses + trucks",
              $"total={prediction.total}");
    }

    private void OnMotorcycleRoutingUpdated(int groundMotorcycles, int flyoverMotorcycles)
    {
        RoutingUpdates++;

        TrafficAPIManager.TrafficPrediction prediction = trafficAPI.CurrentPrediction;
        if (prediction == null)
            return;   // flyover toggled before the first prediction arrived; nothing to check yet

        int bikes = prediction.motorcycles;
        bool flyoverOn = trafficAPI.FlyoverOn;

        // Same rule as Flask's int(bikes * 0.30), worked out independently of TrafficAPIManager.
        double share = Math.Round(trafficAPI.FlyoverBikeRoutingPercentage, 4);
        int expectedFlyover = flyoverOn ? (int)(bikes * share) : 0;

        bool conserved = groundMotorcycles + flyoverMotorcycles == bikes;
        bool splitCorrect = flyoverMotorcycles == expectedFlyover;

        Check(conserved, "Ground + flyover motorcycles = predicted motorcycles",
              $"{groundMotorcycles} + {flyoverMotorcycles} vs {bikes}");
        Check(splitCorrect,
              flyoverOn ? $"Flyover ON sends {share * 100:0}% of motorcycles to the flyover"
                        : "Flyover OFF sends no motorcycles to the flyover",
              $"flyover={flyoverMotorcycles}, expected {expectedFlyover}");

        if (conserved && splitCorrect)
        {
            lastCheckSummary = $"PASS: ground {groundMotorcycles} + flyover {flyoverMotorcycles} = {bikes} " +
                               $"(flyover {(flyoverOn ? "ON" : "OFF")})";
            if (logPassedChecks)
                Debug.Log("TrafficAPITester: " + lastCheckSummary);
        }

        RecordSnapshot(prediction, groundMotorcycles, flyoverMotorcycles, flyoverOn);
    }

    private void OnRequestFailed(string message)
    {
        RequestFailures++;
    }

    private void OnFlyoverChanged(bool isOn)
    {
        Debug.Log($"TrafficAPITester: flyover switched {(isOn ? "ON" : "OFF")}.");
    }

    // ==========================================================================================
    // Checks and before/after comparison
    // ==========================================================================================

    private void Check(bool passed, string rule, string detail)
    {
        if (passed)
        {
            ChecksPassed++;
            return;
        }

        ChecksFailed++;
        lastCheckSummary = $"FAIL: {rule} ({detail})";
        Debug.LogError($"TrafficAPITester FAIL: {rule} ({detail})");
    }

    private void RecordSnapshot(TrafficAPIManager.TrafficPrediction prediction, int ground, int flyover, bool flyoverOn)
    {
        var snapshot = new Snapshot
        {
            valid = true,
            query = $"{prediction.road}|{prediction.day_num}|{prediction.hour}|{prediction.model}",
            totalBikes = prediction.motorcycles,
            ground = ground,
            flyover = flyover
        };

        if (flyoverOn)
            flyoverOnResult = snapshot;
        else
            flyoverOffResult = snapshot;

        UpdateComparison();
    }

    private void UpdateComparison()
    {
        if (!flyoverOffResult.valid || !flyoverOnResult.valid)
        {
            comparisonText = flyoverOffResult.valid
                ? "Have the flyover OFF result. Press F to get the ON result."
                : "Have the flyover ON result. Press F to get the OFF result.";
            return;
        }

        if (flyoverOffResult.query != flyoverOnResult.query)
        {
            comparisonText = "Road/day/hour/model changed. Press F twice to redo the comparison.";
            return;
        }

        double share = Math.Round(trafficAPI.FlyoverBikeRoutingPercentage, 4);
        int before = flyoverOffResult.ground;
        int expectedFlyover = (int)(before * share);

        bool passed = flyoverOffResult.flyover == 0 &&
                      flyoverOnResult.ground + flyoverOnResult.flyover == before &&
                      flyoverOnResult.flyover == expectedFlyover;

        comparisonText =
            $"Before (OFF): ground bikes {before}, flyover 0\n" +
            $"After (ON):   ground bikes {flyoverOnResult.ground}, flyover {flyoverOnResult.flyover}\n" +
            $"Ground bikes reduced by {before - flyoverOnResult.ground} ({share * 100:0}%), " +
            $"total unchanged: {(passed ? "PASS" : "FAIL")}";

        if (comparisonText != lastLoggedComparison)
        {
            lastLoggedComparison = comparisonText;
            string line = "TrafficAPITester before/after flyover:\n" + comparisonText;
            if (passed)
                Debug.Log(line);
            else
                Debug.LogError(line);
        }
    }

    // ==========================================================================================
    // On-screen status box
    // ==========================================================================================

    private void OnGUI()
    {
        if (!showOverlay || trafficAPI == null)
            return;

        if (boxStyle == null)
        {
            boxStyle = new GUIStyle(GUI.skin.box) { padding = new RectOffset(10, 10, 8, 8) };
            textStyle = new GUIStyle(GUI.skin.label) { fontSize = 13, richText = true, wordWrap = true };
            textStyle.normal.textColor = Color.white;
        }

        const float width = 380f;
        GUILayout.BeginArea(new Rect(Screen.width - width - 10f, 10f, width, Screen.height - 20f));
        GUILayout.BeginVertical(boxStyle);
        GUILayout.Label(BuildOverlayText(), textStyle);
        GUILayout.EndVertical();
        GUILayout.EndArea();
    }

    private string BuildOverlayText()
    {
        var p = trafficAPI.CurrentPrediction;
        var text = new System.Text.StringBuilder();

        text.AppendLine("<b>Flask -> Unity connection test</b>");

        if (!string.IsNullOrEmpty(trafficAPI.LastError))
        {
            text.AppendLine("<color=#ff6b6b><b>ERROR</b></color> " + trafficAPI.LastError);
            if (p != null)
                text.AppendLine("<i>Showing the last good prediction below.</i>");
        }
        else if (p == null)
        {
            text.AppendLine(trafficAPI.IsRequestRunning ? "Waiting for Flask..." : "No prediction yet.");
        }
        else
        {
            float age = Time.time - trafficAPI.LastSuccessTime;
            text.AppendLine($"<color=#6bff8f><b>OK</b></color>, updated {age:0}s ago");
        }

        text.AppendLine($"Asking for: {trafficAPI.Road}, {TrafficAPIManager.DayName(trafficAPI.DayNumber)} " +
                        $"{trafficAPI.Hour:00}:00, model {trafficAPI.Model}");

        if (p != null)
        {
            text.AppendLine();
            text.AppendLine($"<b>Predicted per hour</b>{(p.peak_flag == 1 ? " (peak hour)" : "")}");
            text.AppendLine($"Cars {p.cars}   Motorcycles {p.motorcycles}   Buses {p.buses}   Trucks {p.trucks}");
            text.AppendLine($"Total {p.total}");
            text.AppendLine();
            text.AppendLine($"<b>Flyover {(trafficAPI.FlyoverOn ? "ON" : "OFF")}</b>: " +
                            $"ground bikes {trafficAPI.GroundMotorcycles}, flyover bikes {trafficAPI.FlyoverMotorcycles}");
            text.AppendLine($"Dashboard check: Motorcycles should read {p.motorcycles} with flyover OFF " +
                            $"and {GroundIfFlyoverOn(p.motorcycles)} with flyover ON.");
        }

        text.AppendLine();
        text.AppendLine($"<b>Checks</b>: {ChecksPassed} passed, " +
                        $"{(ChecksFailed > 0 ? $"<color=#ff6b6b>{ChecksFailed} failed</color>" : "0 failed")}, " +
                        $"{RequestFailures} request errors");
        text.AppendLine(lastCheckSummary);
        text.AppendLine();
        text.AppendLine("<b>Before / after flyover</b>");
        text.AppendLine(comparisonText);
        text.AppendLine();
        text.Append("<i>Keys: F flyover | R refresh now | T hide this box</i>");

        return text.ToString();
    }

    private int GroundIfFlyoverOn(int motorcycles)
    {
        double share = Math.Round(trafficAPI.FlyoverBikeRoutingPercentage, 4);
        return motorcycles - (int)(motorcycles * share);
    }
}
