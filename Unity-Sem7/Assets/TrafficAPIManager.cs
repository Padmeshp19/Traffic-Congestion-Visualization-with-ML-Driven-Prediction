using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// Fetches traffic predictions from the Flask API and exposes the counts to a Unity spawner.
/// Unity performs the flyover route split locally so the Flask reduction is not applied twice.
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
        public bool flyover;
        public int cars;
        public int motorcycles;
        public int buses;
        public int trucks;
        public int total;
    }

    [Header("Flask API")]
    [SerializeField] private string flaskBaseUrl = "http://127.0.0.1:5000";
    [SerializeField] private string road = "Silk Board Junction";
    [Range(0, 6)] [SerializeField] private int dayNumber = 4;
    [Range(0, 23)] [SerializeField] private int hour = 8;
    [SerializeField] private string model = "gbm";
    [SerializeField] private float refreshIntervalSeconds = 30f;
    [SerializeField] private int requestTimeoutSeconds = 10;

    [Header("Flyover")]
    [SerializeField] private bool flyoverOn;
    [Range(0f, 1f)] [SerializeField] private float flyoverBikeRoutingPercentage = 0.30f;
    [SerializeField] private bool allowKeyboardToggle = true;

    public TrafficPrediction CurrentPrediction { get; private set; }
    public int GroundMotorcycles { get; private set; }
    public int FlyoverMotorcycles { get; private set; }
    public bool FlyoverOn => flyoverOn;
    public bool IsRequestRunning { get; private set; }

    public event Action<TrafficPrediction> PredictionUpdated;
    public event Action<int, int> MotorcycleRoutingUpdated;
    public event Action<string> RequestFailed;

    private Coroutine refreshCoroutine;

    private void Start()
    {
        refreshCoroutine = StartCoroutine(RefreshLoop());
    }

    private void Update()
    {
        if (allowKeyboardToggle && Input.GetKeyDown(KeyCode.F))
            SetFlyover(!flyoverOn);
    }

    private void OnDestroy()
    {
        if (refreshCoroutine != null)
            StopCoroutine(refreshCoroutine);
    }

    private IEnumerator RefreshLoop()
    {
        yield return FetchPrediction();

        while (enabled)
        {
            yield return new WaitForSeconds(Mathf.Max(1f, refreshIntervalSeconds));
            yield return FetchPrediction();
        }
    }

    public void RefreshNow()
    {
        StartCoroutine(FetchPrediction());
    }

    public void SetFlyover(bool enabled)
    {
        flyoverOn = enabled;
        RecalculateRouting();
        Debug.Log($"TrafficAPIManager: Flyover {(flyoverOn ? "ON" : "OFF")}; " +
                  $"ground motorcycles={GroundMotorcycles}, flyover motorcycles={FlyoverMotorcycles}");
    }

    private IEnumerator FetchPrediction()
    {
        if (IsRequestRunning)
            yield break;

        IsRequestRunning = true;

        // The Flask /predict route is POST + JSON. Send flyover=false because Unity
        // performs the 30% route split and must not apply Flask's reduction twice.
        var requestBody = new PredictionRequest
        {
            road = road,
            day_num = dayNumber,
            hour = hour,
            flyover = false,
            model = model
        };

        string json = JsonUtility.ToJson(requestBody);
        byte[] body = Encoding.UTF8.GetBytes(json);

        using (UnityWebRequest request = new UnityWebRequest(
                   $"{flaskBaseUrl.TrimEnd('/')}/predict", "POST"))
        {
            request.uploadHandler = new UploadHandlerRaw(body);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.timeout = Mathf.Max(1, requestTimeoutSeconds);
            request.SetRequestHeader("Content-Type", "application/json");
            request.SetRequestHeader("Accept", "application/json");

            yield return request.SendWebRequest();
            IsRequestRunning = false;

            if (request.result != UnityWebRequest.Result.Success)
            {
                string message = $"Flask /predict failed ({request.responseCode}): " +
                                 $"{request.error}. Response: {request.downloadHandler.text}";
                Debug.LogError(message);
                RequestFailed?.Invoke(message);
                yield break;
            }

            TrafficPrediction prediction;
            try
            {
                prediction = JsonUtility.FromJson<TrafficPrediction>(request.downloadHandler.text);
            }
            catch (Exception exception)
            {
                string message = $"Could not parse Flask prediction JSON: {exception.Message}";
                Debug.LogError(message + $" Response: {request.downloadHandler.text}");
                RequestFailed?.Invoke(message);
                yield break;
            }

            if (prediction == null)
            {
                const string message = "Flask returned an empty prediction object.";
                Debug.LogError(message);
                RequestFailed?.Invoke(message);
                yield break;
            }

            prediction.cars = Mathf.Max(0, prediction.cars);
            prediction.motorcycles = Mathf.Max(0, prediction.motorcycles);
            prediction.buses = Mathf.Max(0, prediction.buses);
            prediction.trucks = Mathf.Max(0, prediction.trucks);
            prediction.total = prediction.cars + prediction.motorcycles +
                               prediction.buses + prediction.trucks;

            CurrentPrediction = prediction;
            RecalculateRouting();
            PredictionUpdated?.Invoke(CurrentPrediction);

            Debug.Log($"TrafficAPIManager: received {prediction.road} prediction " +
                      $"cars={prediction.cars}, motorcycles={prediction.motorcycles}, " +
                      $"buses={prediction.buses}, trucks={prediction.trucks}, " +
                      $"ground motorcycles={GroundMotorcycles}, " +
                      $"flyover motorcycles={FlyoverMotorcycles}");
        }
    }

    private void RecalculateRouting()
    {
        int motorcycles = CurrentPrediction == null ? 0 : CurrentPrediction.motorcycles;

        if (!flyoverOn)
        {
            GroundMotorcycles = motorcycles;
            FlyoverMotorcycles = 0;
        }
        else
        {
            FlyoverMotorcycles = Mathf.FloorToInt(
                motorcycles * Mathf.Clamp01(flyoverBikeRoutingPercentage));
            GroundMotorcycles = motorcycles - FlyoverMotorcycles;
        }

        MotorcycleRoutingUpdated?.Invoke(GroundMotorcycles, FlyoverMotorcycles);
    }
}
