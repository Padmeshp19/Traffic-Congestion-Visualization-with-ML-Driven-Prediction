using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// Fetches traffic predictions from the Flask API and shares the counts with other Unity scripts
/// (the junction spawner, the flyover simulation and the UI panel).
///
/// Unity always asks Flask for the prediction WITHOUT the flyover (flyover = false) and performs
/// the motorcycle route split itself, so the 30% reduction is never applied twice.
///
/// Notifications, in the order they are sent after every successful refresh:
///   1. PredictionUpdated         - cars, motorcycles (total), buses, trucks, total, road, day, hour ...
///   2. MotorcycleRoutingUpdated  - (groundMotorcycles, flyoverMotorcycles)
/// MotorcycleRoutingUpdated is also sent on its own whenever the flyover is switched ON/OFF.
/// FlyoverChanged is sent when the flyover state changes. RequestFailed is sent on any error.
///
/// The same prediction is re-sent on every refresh (default every 30 s). Listeners should treat
/// the counts as "how many there should be", never as "add this many more".
/// </summary>
public class TrafficAPIManager : MonoBehaviour
{
    [Serializable]
    public class PredictionRequest
    {
        public string road;
        public int day_num;
        public int hour;
        public bool flyover;
        public string model;
    }

    [Serializable]
    public class TrafficPrediction
    {
        public string model;
        public string road;
        public int day_num;
        public int hour;
        public int peak_flag;

        /// <summary>Always false: Unity asks Flask without the flyover. Use TrafficAPIManager.FlyoverOn instead.</summary>
        public bool flyover;

        public int cars;
        public int motorcycles;   // ALL predicted motorcycles (ground + flyover)
        public int buses;
        public int trucks;
        public int total;
    }

    [Serializable]
    private class ErrorResponse
    {
        public string error = null;   // filled in by JsonUtility
    }

    public static readonly string[] DayNames =
        { "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday" };

    private static readonly string[] RequiredResponseFields =
        { "\"cars\"", "\"motorcycles\"", "\"buses\"", "\"trucks\"" };

    [Header("Flask API")]
    [SerializeField] private string flaskBaseUrl = "http://127.0.0.1:5000";
    [Tooltip("Must match a road name on the dashboard exactly, e.g. Silk Board Junction.")]
    [SerializeField] private string road = "Silk Board Junction";
    [Tooltip("0 = Monday ... 6 = Sunday")]
    [Range(0, 6)] [SerializeField] private int dayNumber = 4;
    [Range(0, 23)] [SerializeField] private int hour = 8;
    [Tooltip("gbm or rf")]
    [SerializeField] private string model = "gbm";
    [SerializeField] private float refreshIntervalSeconds = 30f;
    [SerializeField] private int requestTimeoutSeconds = 10;
    [Tooltip("In Play mode, fetch a new prediction as soon as Road, Day, Hour, Model or the URL is changed.")]
    [SerializeField] private bool refreshWhenSettingsChange = true;

    [Header("Flyover")]
    [SerializeField] private bool flyoverOn;
    [Range(0f, 1f)] [SerializeField] private float flyoverBikeRoutingPercentage = 0.30f;

    [Header("Keyboard (Play mode, click inside the Game view first)")]
    [SerializeField] private bool allowKeyboardToggle = true;
    [SerializeField] private KeyCode flyoverToggleKey = KeyCode.F;
    [SerializeField] private bool allowKeyboardRefresh = true;
    [SerializeField] private KeyCode refreshKey = KeyCode.R;

    [Header("Logging")]
    [Tooltip("Log every refresh. When off, only predictions that changed are logged.")]
    [SerializeField] private bool logEveryRefresh;

    // ---- Latest data -------------------------------------------------------------------------
    public TrafficPrediction CurrentPrediction { get; private set; }
    public int GroundMotorcycles { get; private set; }
    public int FlyoverMotorcycles { get; private set; }
    public bool FlyoverOn => flyoverOn;
    public bool IsRequestRunning { get; private set; }

    // ---- Status (useful for the UI panel) ----------------------------------------------------
    public bool HasPrediction => CurrentPrediction != null;
    /// <summary>True if the last successful refresh returned different numbers or settings than the one before.</summary>
    public bool LastPredictionChanged { get; private set; }
    /// <summary>Message of the last failed request; null after a successful one.</summary>
    public string LastError { get; private set; }
    /// <summary>Time.time of the last successful refresh, or -1 if none yet.</summary>
    public float LastSuccessTime { get; private set; } = -1f;

    // ---- Current settings (what Unity is asking Flask for) -----------------------------------
    public string Road => road;
    public int DayNumber => dayNumber;
    public int Hour => hour;
    public string Model => model;
    public string FlaskBaseUrl => flaskBaseUrl;
    public float FlyoverBikeRoutingPercentage => Mathf.Clamp01(flyoverBikeRoutingPercentage);

    public event Action<TrafficPrediction> PredictionUpdated;
    public event Action<int, int> MotorcycleRoutingUpdated;
    public event Action<string> RequestFailed;
    public event Action<bool> FlyoverChanged;

    private UnityWebRequest activeRequest;
    private string lastRequestedQuery;
    private bool refreshQueued;
    private bool appliedFlyoverOn;
    private float appliedRoutingPercentage;

    public static string DayName(int day)
    {
        return day >= 0 && day < DayNames.Length ? DayNames[day] : $"Day {day}";
    }

    // ==========================================================================================
    // Unity lifecycle
    // ==========================================================================================

    private void OnEnable()
    {
        appliedFlyoverOn = flyoverOn;
        appliedRoutingPercentage = FlyoverBikeRoutingPercentage;
        StartCoroutine(RefreshLoop());
    }

    private void OnDisable()
    {
        // Stops the refresh loop and any request that is still waiting for Flask.
        StopAllCoroutines();

        if (activeRequest != null)
        {
            activeRequest.Abort();
            activeRequest.Dispose();
            activeRequest = null;
        }

        IsRequestRunning = false;
        refreshQueued = false;
    }

    private void OnValidate()
    {
        refreshIntervalSeconds = Mathf.Max(1f, refreshIntervalSeconds);
        requestTimeoutSeconds = Mathf.Max(1, requestTimeoutSeconds);
    }

    private void Update()
    {
#if ENABLE_LEGACY_INPUT_MANAGER
        if (allowKeyboardToggle && Input.GetKeyDown(flyoverToggleKey))
            SetFlyover(!flyoverOn);

        if (allowKeyboardRefresh && Input.GetKeyDown(refreshKey))
            RefreshNow();
#endif

        // The Flyover tick box or percentage was changed in the Inspector during Play mode.
        if (flyoverOn != appliedFlyoverOn)
            SetFlyover(flyoverOn);
        else if (!Mathf.Approximately(FlyoverBikeRoutingPercentage, appliedRoutingPercentage))
            RecalculateRouting(true);

        // Road / Day / Hour / Model / URL was changed in the Inspector during Play mode.
        if (refreshWhenSettingsChange && !IsRequestRunning && BuildQueryKey() != lastRequestedQuery)
            RefreshNow();
    }

    // ==========================================================================================
    // Public controls (for the UI panel or other scripts)
    // ==========================================================================================

    /// <summary>Ask Flask for a new prediction now instead of waiting for the next refresh.</summary>
    public void RefreshNow()
    {
        if (!isActiveAndEnabled)
        {
            Debug.LogWarning("TrafficAPIManager: cannot refresh while the component or its GameObject is disabled.");
            return;
        }

        if (IsRequestRunning)
        {
            refreshQueued = true;   // run again as soon as the current request finishes
            return;
        }

        StartCoroutine(FetchPrediction());
    }

    /// <summary>Change what Unity asks Flask for, and fetch the new prediction straight away.</summary>
    public void SetQuery(string newRoad, int newDayNumber, int newHour)
    {
        if (!string.IsNullOrWhiteSpace(newRoad))
            road = newRoad.Trim();

        dayNumber = Mathf.Clamp(newDayNumber, 0, 6);
        hour = Mathf.Clamp(newHour, 0, 23);
        RefreshNow();
    }

    /// <summary>Switch between "gbm" and "rf" and fetch the new prediction straight away.</summary>
    public void SetModel(string newModel)
    {
        if (!string.IsNullOrWhiteSpace(newModel))
            model = newModel.Trim().ToLowerInvariant();

        RefreshNow();
    }

    public void SetFlyover(bool isOn)
    {
        bool changed = appliedFlyoverOn != isOn;
        flyoverOn = isOn;
        appliedFlyoverOn = isOn;

        RecalculateRouting(true);

        Debug.Log($"TrafficAPIManager: Flyover {(flyoverOn ? "ON" : "OFF")}; " +
                  $"ground motorcycles={GroundMotorcycles}, flyover motorcycles={FlyoverMotorcycles}");

        if (changed)
            FlyoverChanged?.Invoke(flyoverOn);
    }

    // ==========================================================================================
    // Fetching
    // ==========================================================================================

    private IEnumerator RefreshLoop()
    {
        while (true)
        {
            RefreshNow();
            yield return new WaitForSeconds(Mathf.Max(1f, refreshIntervalSeconds));
        }
    }

    private IEnumerator FetchPrediction()
    {
        if (IsRequestRunning)
        {
            refreshQueued = true;
            yield break;
        }

        string queryKey = BuildQueryKey();
        lastRequestedQuery = queryKey;
        refreshQueued = false;

        if (string.IsNullOrWhiteSpace(road))
        {
            Fail("Road is empty. Type a road name in the TrafficAPIManager Inspector (e.g. Silk Board Junction).");
            yield break;
        }

        IsRequestRunning = true;

        // Always flyover = false: Unity performs the route split and must not apply Flask's reduction too.
        var requestBody = new PredictionRequest
        {
            road = road,
            day_num = dayNumber,
            hour = hour,
            flyover = false,
            model = model
        };

        byte[] body = Encoding.UTF8.GetBytes(JsonUtility.ToJson(requestBody));
        string url = $"{flaskBaseUrl.TrimEnd('/')}/predict";

        UnityWebRequest request = null;
        UnityWebRequestAsyncOperation operation = null;
        string startError = null;

        // SendWebRequest can throw straight away (e.g. Unity blocking http://). Catch it so the
        // manager reports a clear error instead of getting stuck with IsRequestRunning = true.
        try
        {
            request = new UnityWebRequest(url, "POST");
            request.uploadHandler = new UploadHandlerRaw(body);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.timeout = Mathf.Max(1, requestTimeoutSeconds);
            request.SetRequestHeader("Content-Type", "application/json");
            request.SetRequestHeader("Accept", "application/json");
            activeRequest = request;
            operation = request.SendWebRequest();
        }
        catch (Exception exception)
        {
            startError = DescribeStartFailure(url, exception);
        }

        if (startError != null)
        {
            if (request != null)
                request.Dispose();
            activeRequest = null;
            IsRequestRunning = false;
            Fail(startError);
            yield break;
        }

        yield return operation;

        // Copy what is needed, then free the request.
        UnityWebRequest.Result result = request.result;
        long responseCode = request.responseCode;
        string error = request.error;
        string responseText = request.downloadHandler != null ? request.downloadHandler.text : string.Empty;
        request.Dispose();
        activeRequest = null;
        IsRequestRunning = false;

        if (queryKey != BuildQueryKey())
        {
            // The settings changed while waiting, so this answer is out of date. Ask again.
            if (logEveryRefresh)
                Debug.Log("TrafficAPIManager: settings changed during the request; fetching again.");
            refreshQueued = true;
        }
        else if (result != UnityWebRequest.Result.Success)
        {
            Fail(DescribeHttpFailure(url, responseCode, error, responseText));
        }
        else if (!TryParsePrediction(responseText, out TrafficPrediction prediction, out string parseError))
        {
            Fail(parseError);
        }
        else
        {
            ApplyPrediction(prediction);
        }

        if (refreshQueued && isActiveAndEnabled)
        {
            refreshQueued = false;
            StartCoroutine(FetchPrediction());
        }
    }

    private void ApplyPrediction(TrafficPrediction prediction)
    {
        prediction.cars = Mathf.Max(0, prediction.cars);
        prediction.motorcycles = Mathf.Max(0, prediction.motorcycles);
        prediction.buses = Mathf.Max(0, prediction.buses);
        prediction.trucks = Mathf.Max(0, prediction.trucks);
        prediction.total = prediction.cars + prediction.motorcycles + prediction.buses + prediction.trucks;

        LastPredictionChanged = !IsSamePrediction(CurrentPrediction, prediction);
        CurrentPrediction = prediction;
        LastError = null;
        LastSuccessTime = Time.time;

        RecalculateRouting(false);

        // Order matters for listeners: full prediction first, then the motorcycle split.
        PredictionUpdated?.Invoke(CurrentPrediction);
        MotorcycleRoutingUpdated?.Invoke(GroundMotorcycles, FlyoverMotorcycles);

        if (LastPredictionChanged || logEveryRefresh)
        {
            Debug.Log($"TrafficAPIManager: received {prediction.road} prediction " +
                      $"({DayName(prediction.day_num)} {prediction.hour:00}:00, {prediction.model}) " +
                      $"cars={prediction.cars}, motorcycles={prediction.motorcycles}, " +
                      $"buses={prediction.buses}, trucks={prediction.trucks}, total={prediction.total}, " +
                      $"ground motorcycles={GroundMotorcycles}, flyover motorcycles={FlyoverMotorcycles}");
        }
    }

    private void RecalculateRouting(bool notify)
    {
        int motorcycles = CurrentPrediction == null ? 0 : CurrentPrediction.motorcycles;
        appliedRoutingPercentage = FlyoverBikeRoutingPercentage;

        if (!flyoverOn)
        {
            GroundMotorcycles = motorcycles;
            FlyoverMotorcycles = 0;
        }
        else
        {
            FlyoverMotorcycles = Mathf.FloorToInt(motorcycles * appliedRoutingPercentage);
            GroundMotorcycles = motorcycles - FlyoverMotorcycles;
        }

        if (notify)
            MotorcycleRoutingUpdated?.Invoke(GroundMotorcycles, FlyoverMotorcycles);
    }

    private void Fail(string message)
    {
        LastError = message;
        Debug.LogError("TrafficAPIManager: " + message);
        RequestFailed?.Invoke(message);
    }

    // ==========================================================================================
    // Helpers
    // ==========================================================================================

    private string BuildQueryKey()
    {
        return $"{flaskBaseUrl}|{road}|{dayNumber}|{hour}|{model}";
    }

    private static bool IsSamePrediction(TrafficPrediction a, TrafficPrediction b)
    {
        if (a == null || b == null)
            return false;

        return a.road == b.road && a.day_num == b.day_num && a.hour == b.hour && a.model == b.model &&
               a.cars == b.cars && a.motorcycles == b.motorcycles && a.buses == b.buses && a.trucks == b.trucks;
    }

    private static bool TryParsePrediction(string json, out TrafficPrediction prediction, out string error)
    {
        prediction = null;
        error = null;

        if (string.IsNullOrWhiteSpace(json))
        {
            error = "Flask returned an empty response.";
            return false;
        }

        foreach (string field in RequiredResponseFields)
        {
            if (!json.Contains(field))
            {
                error = $"Flask response is missing {field}. Response: {Shorten(json)}";
                return false;
            }
        }

        try
        {
            prediction = JsonUtility.FromJson<TrafficPrediction>(json);
        }
        catch (Exception exception)
        {
            error = $"Could not parse Flask prediction JSON: {exception.Message}. Response: {Shorten(json)}";
            return false;
        }

        if (prediction == null)
        {
            error = "Flask returned an empty prediction object.";
            return false;
        }

        return true;
    }

    private static string DescribeHttpFailure(string url, long responseCode, string error, string responseText)
    {
        if (responseCode == 0)
            return $"Could not reach Flask at {url} ({error}). Is 'python app.py' running, and is the Flask Base URL correct?";

        if (responseCode == 400)
            return $"Flask rejected the request (400): {ExtractServerError(responseText)}. " +
                   "Check that the Road matches a dashboard road exactly and the Model is gbm or rf.";

        if (responseCode >= 500)
            return $"Flask hit an internal error ({responseCode}). Check the Flask terminal for the Python error.";

        return $"Flask /predict failed ({responseCode}): {error}. Response: {Shorten(responseText)}";
    }

    private static string DescribeStartFailure(string url, Exception exception)
    {
        if (exception.Message.IndexOf("Insecure connection", StringComparison.OrdinalIgnoreCase) >= 0)
            return "Unity is blocking plain http:// connections, so it cannot reach Flask. Fix: Edit > Project Settings > " +
                   "Player > Other Settings > 'Allow downloads over HTTP' = 'Always allowed', then press Play again.";

        return $"Could not start the request to {url}: {exception.Message}. Check the Flask Base URL.";
    }

    private static string ExtractServerError(string responseText)
    {
        try
        {
            var parsed = JsonUtility.FromJson<ErrorResponse>(responseText);
            if (parsed != null && !string.IsNullOrEmpty(parsed.error))
                return parsed.error;
        }
        catch (Exception)
        {
            // Not JSON; fall through and show the raw text.
        }

        return Shorten(responseText);
    }

    private static string Shorten(string text)
    {
        if (string.IsNullOrEmpty(text))
            return "(empty)";
        return text.Length <= 300 ? text : text.Substring(0, 300) + "...";
    }
}
