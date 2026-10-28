using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Puts the predicted traffic on the junction.
///
///   predicted vehicles per hour  /  600, rounded  =  target on screen for each type
///
/// The number of each type on the road is kept equal to its target:
///   - too few: new vehicles are placed where there is room (never overlapping, never in the crossing);
///   - too many: the extras drive on and are not replaced when they reach the end;
///   - a vehicle that reaches the end is replaced by a new one at the start.
/// The same prediction arriving again every 30 s changes nothing.
///
/// Counts come from typed-in test numbers (keys 1-5 pick presets) or from TrafficAPIManager.
/// Motorcycles use the GROUND count only; flyover bikes belong to the flyover simulation.
///
/// HOUR COUNTER: a clock plays the predicted hour (e.g. 08:00 -> 09:00) in a few real minutes.
///   - Reached end points (exact): +1 every time a vehicle drives onto an end point. Nothing scaled.
///     (Extra vehicles from earlier traffic that are driving off are not counted.)
///   - Counted for the hour: each exit is worth  600 x (measured crossing time) / (hour length in s)
///     real vehicles. One vehicle on screen stands for 600 per hour and makes one trip every
///     "crossing time" seconds, so over the hour its trips add up to about 600. Slower traffic gives
///     fewer exits, each worth more. If the road cannot hold all the vehicles the prediction needs
///     (they wait to enter, or the 70-vehicle cap), the count falls behind.
///   - At this rate, 1 hour = counted so far / share of the hour gone, compared with the dashboard.
///   - Flyover bikes report each lap (FlyoverBikeSimulation) and are counted the same way.
///   - At the end of the hour the numbers stop and stay on screen. C (or the Reset button) starts the
///     hour again; a new prediction, a flyover switch or other test numbers also start it again.
/// </summary>
public class JunctionSpawner : MonoBehaviour
{
    public enum CountSource
    {
        TestNumbers,
        LivePrediction
    }

    [Serializable]
    public class TestPreset
    {
        public string name;
        public int cars, bikes, buses, trucks;
    }

    [Header("Where vehicles drive (leave empty to use every JunctionLane in the scene)")]
    public JunctionLane[] lanes;

    [Header("Vehicle templates (drag all of Assets/JunctionSim/Prefabs here)")]
    public VehicleInfo[] templates;

    [Header("Scale")]
    [Tooltip("1 vehicle on screen stands for this many predicted vehicles per hour.")]
    public float vehiclesPerHourPerSpawned = 600f;
    [Tooltip("Upper limit on screen. If the targets add up to more, every type is scaled down by the same factor.")]
    public int maxVehiclesOnRoad = 70;

    [Header("Hour counter")]
    [Tooltip("Real seconds the on-screen clock takes to go through one hour (300 = 5 minutes).")]
    public float secondsPerSimulatedHour = 300f;
    [Tooltip("Hour the clock shows for test numbers (keys 1-5 set it from the preset name). Live predictions use their own hour.")]
    public int testHour = 8;
    [Tooltip("Key that resets the counter and starts the hour again. The readout box also has a Reset button.")]
    public KeyCode resetCounterKey = KeyCode.C;

    [Header("Where the counts come from")]
    public CountSource source = CountSource.TestNumbers;
    [Tooltip("Leave empty to find it in the scene. Only used with Live Prediction.")]
    public TrafficAPIManager trafficAPI;

    [Header("Test numbers (vehicles per hour, like the model's output)")]
    public int testCars = 7104;
    public int testBikes = 16870;
    public int testBuses = 1660;
    public int testTrucks = 6061;
    [Tooltip("Keys 1-5 load these in Play mode. Numbers are Silk Board Junction, GBM.")]
    public TestPreset[] presets =
    {
        new TestPreset { name = "Low (Fri 3 AM)",       cars = 539,   bikes = 1306,  buses = 77,   trucks = 483 },
        new TestPreset { name = "Medium (Sun 2 PM)",    cars = 3887,  bikes = 9063,  buses = 915,  trucks = 3293 },
        new TestPreset { name = "Peak (Fri 8 AM)",      cars = 7104,  bikes = 16870, buses = 1660, trucks = 6061 },
        new TestPreset { name = "Busiest (Fri 6 PM)",   cars = 7980,  bikes = 19189, buses = 1817, trucks = 6912 },
        new TestPreset { name = "Stress (over the cap)", cars = 30000, bikes = 60000, buses = 6000, trucks = 24000 },
    };

    [Header("Behaviour")]
    [Tooltip("When targets go up, place the new vehicles anywhere there is room, so the road fills at once. Otherwise they enter at the start.")]
    public bool fillInstantlyWhenTargetsRise = true;
    [Tooltip("Remove extra vehicles at once instead of letting them drive to the end (handy for testing).")]
    public bool removeExtrasInstantly;
    [Tooltip("Seconds between attempts to place vehicles that are waiting for room.")]
    public float retryInterval = 0.2f;
    [Tooltip("Switch off the hand-placed vehicles already on these lanes while in Play mode, so they do not spoil the counts.")]
    public bool switchOffOldVehicles = true;

    [Header("Readout")]
    public bool showReadout = true;
    public KeyCode toggleReadoutKey = KeyCode.G;

    private static readonly VehicleType[] Types = { VehicleType.Car, VehicleType.Bike, VehicleType.Bus, VehicleType.Truck };

    private readonly Dictionary<VehicleType, List<LaneCarController>> active = new Dictionary<VehicleType, List<LaneCarController>>();
    private readonly HashSet<LaneCarController> leaving = new HashSet<LaneCarController>();
    private readonly HashSet<LaneCarController> spawnedByMe = new HashSet<LaneCarController>();
    private readonly Dictionary<VehicleType, int> predicted = new Dictionary<VehicleType, int>();
    private readonly Dictionary<VehicleType, int> target = new Dictionary<VehicleType, int>();
    private readonly Dictionary<VehicleType, Transform> groups = new Dictionary<VehicleType, Transform>();
    private readonly Dictionary<VehicleType, List<VehicleInfo>> templatesByType = new Dictionary<VehicleType, List<VehicleInfo>>();

    private bool countsDirty = true;
    private bool fillAnywhere = true;
    private float nextAttempt;
    private int spawnCounter;
    private string sourceLabel = "";
    private string capNote = "";
    private string lastError;
    private int predictedFlyoverBikes;
    private bool flyoverOn;
    private CountSource appliedSource;
    private int[] appliedTestNumbers = new int[5];
    private readonly HashSet<string> warnedOnce = new HashSet<string>();
    private readonly List<string> problems = new List<string>();
    private Transform container;
    private GUIStyle readoutStyle;

    /// <summary>Sent whenever the targets change (useful for the UI panel).</summary>
    public event Action CountsChanged;

    // ---- Hour counter -----------------------------------------------------------------------
    private readonly Dictionary<VehicleType, int> exits = new Dictionary<VehicleType, int>();               // exact, this hour
    private readonly Dictionary<VehicleType, float> tripSeconds = new Dictionary<VehicleType, float>();     // measured full trips: total seconds
    private readonly Dictionary<VehicleType, int> trips = new Dictionary<VehicleType, int>();               // measured full trips: how many
    private readonly Dictionary<LaneCarController, float> enteredAt = new Dictionary<LaneCarController, float>();
    private int flyoverLaps;                    // exact, this hour
    private float flyoverLapSeconds;            // measured full laps: total seconds
    private int flyoverLapsTimed;
    private float flyoverLapEstimate;           // route length / speed, until a full lap is measured
    private float flyoverVehiclesPerBike = 600f;
    private float hourElapsed;
    private int hourStart = 8;

    private const int TripsBeforeMeasured = 3;
    private static readonly Regex HourInName = new Regex(@"(\d{1,2})\s*(AM|PM)", RegexOptions.IgnoreCase);

    /// <summary>Sent every time a vehicle or flyover bike is counted.</summary>
    public event Action PassedChanged;

    /// <summary>Real seconds one simulated hour takes.</summary>
    public float SecondsPerHour => Mathf.Max(10f, secondsPerSimulatedHour);
    /// <summary>0 at the start of the hour, 1 at the end.</summary>
    public float HourProgress => Mathf.Clamp01(hourElapsed / SecondsPerHour);
    /// <summary>True once the clock has reached the end of the hour; the numbers then stay as they are.</summary>
    public bool HourComplete => ForecastTotal > 0 && hourElapsed >= SecondsPerHour;
    /// <summary>The clock runs and exits are counted while there is traffic and the hour is not over.</summary>
    public bool Counting => ForecastTotal > 0 && !HourComplete;
    public int HourStart => hourStart;
    /// <summary>The simulated time, e.g. "08:34".</summary>
    public string ClockText
    {
        get
        {
            int minutes = Mathf.Min(60, Mathf.FloorToInt(HourProgress * 60f));
            int total = (hourStart * 60 + minutes) % 1440;
            return $"{total / 60:00}:{total % 60:00}";
        }
    }
    public string HourLabel => $"{hourStart % 24:00}:00-{(hourStart + 1) % 24:00}:00";

    /// <summary>Vehicles of this type that drove onto an end point this hour (exact, not scaled).</summary>
    public int ReachedEnd(VehicleType type) => exits.TryGetValue(type, out int n) ? n : 0;
    public int ReachedEndTotal => Types.Sum(ReachedEnd);
    /// <summary>Flyover bike laps finished this hour (exact, not scaled).</summary>
    public int FlyoverLaps => flyoverLaps;

    /// <summary>True once enough vehicles of this type have driven the whole road to use their measured time.</summary>
    public bool CrossingTimeMeasured(VehicleType type) => trips.TryGetValue(type, out int n) && n >= TripsBeforeMeasured;
    /// <summary>Average seconds from entering the road to reaching the end (measured; estimated until measured).</summary>
    public float AverageCrossingSeconds(VehicleType type) =>
        CrossingTimeMeasured(type) ? tripSeconds[type] / trips[type] : EstimatedCrossingSeconds(type);
    /// <summary>Real vehicles one exit stands for: 600 x crossing time / hour length.</summary>
    public float WorthPerExit(VehicleType type) => Mathf.Max(1f, vehiclesPerHourPerSpawned) * AverageCrossingSeconds(type) / SecondsPerHour;
    /// <summary>Average seconds a flyover bike takes for one lap (measured; route length / speed until measured).</summary>
    public float AverageFlyoverLapSeconds => flyoverLapsTimed > 0 ? flyoverLapSeconds / flyoverLapsTimed : flyoverLapEstimate;
    /// <summary>Real vehicles one flyover lap stands for: 600 x lap time / hour length.</summary>
    public float WorthPerFlyoverLap => flyoverVehiclesPerBike * AverageFlyoverLapSeconds / SecondsPerHour;

    /// <summary>Real vehicles of this type counted this hour (exits x worth per exit).</summary>
    public int Counted(VehicleType type) => Mathf.RoundToInt(ReachedEnd(type) * WorthPerExit(type));
    public int CountedGround => Types.Sum(Counted);
    public int CountedFlyover => Mathf.RoundToInt(flyoverLaps * WorthPerFlyoverLap);
    public int CountedTotal => CountedGround + CountedFlyover;

    /// <summary>The dashboard's hourly number for the ground road (after the flyover takes its bikes).</summary>
    public int ForecastGround => Types.Sum(Predicted);
    /// <summary>The dashboard's hourly number for the whole junction (before the flyover).</summary>
    public int ForecastTotal => Types.Sum(Predicted) + predictedFlyoverBikes;
    /// <summary>What the whole hour comes to at the rate counted so far; -1 until 10% of the hour has gone.</summary>
    public int ProjectedHourTotal => HourComplete ? CountedTotal
                                   : HourProgress >= 0.1f ? Mathf.RoundToInt(CountedTotal / HourProgress) : -1;
    /// <summary>ProjectedHourTotal as a % of the dashboard; -1 until 10% of the hour has gone.</summary>
    public float MatchPercent => ProjectedHourTotal < 0 || ForecastTotal <= 0 ? -1f : 100f * ProjectedHourTotal / ForecastTotal;

    // Older names, still used by TrafficHUD.
    public int Passed(VehicleType type) => Counted(type);
    public int PassedFlyoverBikes => CountedFlyover;
    public int PassedAllBikes => Counted(VehicleType.Bike) + CountedFlyover;
    public int ForecastAllBikes => Predicted(VehicleType.Bike) + predictedFlyoverBikes;
    public int PassedTotal => CountedTotal;

    /// <summary>
    /// Called by the flyover simulation each time a flyover bike finishes a lap.
    /// measuredSeconds: how long that lap took (0 if unknown, e.g. the first lap, which starts part-way round).
    /// fullLapSeconds: route length / speed. vehiclesPerHourPerBike: what one flyover bike stands for (600).
    /// </summary>
    public void RegisterFlyoverLap(float measuredSeconds, float fullLapSeconds, float vehiclesPerHourPerBike)
    {
        if (!Counting)
            return;

        flyoverLaps++;
        flyoverVehiclesPerBike = Mathf.Max(1f, vehiclesPerHourPerBike);
        if (measuredSeconds > 0f)
        {
            flyoverLapSeconds += measuredSeconds;
            flyoverLapsTimed++;
        }
        else if (fullLapSeconds > 0f)
        {
            flyoverLapEstimate = fullLapSeconds;
        }
        PassedChanged?.Invoke();
    }

    /// <summary>Older name: one flyover lap with no timing (counted at the average lap time).</summary>
    public void RegisterFlyoverPass(int vehiclesRepresented) => RegisterFlyoverLap(0f, 0f, vehiclesRepresented);

    /// <summary>Counter back to zero and the clock back to the start of the hour.</summary>
    [ContextMenu("Reset hour counter")]
    public void ResetPassed()
    {
        foreach (VehicleType type in Types)
            exits[type] = 0;
        flyoverLaps = 0;
        hourElapsed = 0f;
        PassedChanged?.Invoke();
    }

    /// <summary>Forget the measured crossing and lap times (the traffic changed, so they will be different).</summary>
    private void ResetCrossingTimes()
    {
        foreach (VehicleType type in Types)
        {
            tripSeconds[type] = 0f;
            trips[type] = 0;
        }
        flyoverLapSeconds = 0f;
        flyoverLapsTimed = 0;
    }

    /// <summary>
    /// Until a few vehicles have driven the whole road: lane length at a speed half way between the
    /// vehicle's own and the slowest vehicle using that lane (no overtaking), plus the average red-light wait.
    /// </summary>
    private float EstimatedCrossingSeconds(VehicleType type)
    {
        if (!templatesByType.TryGetValue(type, out List<VehicleInfo> own) || own.Count == 0 || lanes == null)
            return 0f;
        List<JunctionLane> allowed = lanes.Where(lane => lane != null && lane.Allows(type)).ToList();
        float ownSpeed = own.Average(t => t.baseSpeed);
        if (allowed.Count == 0 || ownSpeed <= 0.01f)
            return 0f;

        float half = own.Average(t => t.HalfLength);
        float total = 0f;
        foreach (JunctionLane lane in allowed)
        {
            float slowest = ownSpeed;
            foreach (VehicleType other in Types)
            {
                if (lane.Allows(other) && Predicted(other) > 0 && templatesByType[other].Count > 0)
                    slowest = Mathf.Min(slowest, templatesByType[other].Average(t => t.baseSpeed));
            }

            float distance = Mathf.Max(0f, lane.Length - half - 0.05f - 2f);   // the vehicle leaves 2 before the end point
            float redWait = 0f;
            if (lane.signal != null)
            {
                float cycle = lane.signal.redTime + lane.signal.yellowTime + lane.signal.greenTime;
                redWait = cycle > 0f ? lane.signal.redTime * lane.signal.redTime / (2f * cycle) : 0f;
            }
            total += 0.5f * (distance / ownSpeed + distance / Mathf.Max(0.01f, slowest)) + redWait;
        }
        return total / allowed.Count;
    }

    // ---- Numbers for the UI panel -------------------------------------------------------------
    public int Predicted(VehicleType type) => predicted.TryGetValue(type, out int n) ? n : 0;
    public int Target(VehicleType type) => target.TryGetValue(type, out int n) ? n : 0;
    public int OnRoad(VehicleType type) => active.TryGetValue(type, out var list) ? list.Count : 0;
    public int Leaving(VehicleType type) => active.TryGetValue(type, out var list) ? list.Count(v => leaving.Contains(v)) : 0;
    public int Waiting(VehicleType type) => Mathf.Max(0, Target(type) - (OnRoad(type) - Leaving(type)));
    public int PredictedFlyoverBikes => predictedFlyoverBikes;
    /// <summary>All predicted motorcycles, ground + flyover (same as the dashboard with the flyover OFF).</summary>
    public int PredictedAllBikes => source == CountSource.LivePrediction ? liveAllBikes : Predicted(VehicleType.Bike);
    public bool FlyoverOn => flyoverOn;
    public bool IsCapped => capNote.Length > 0;
    public string SourceLabel => sourceLabel;

    // ==========================================================================================
    // Start-up
    // ==========================================================================================

    private void Awake()
    {
        foreach (VehicleType type in Types)
        {
            active[type] = new List<LaneCarController>();
            predicted[type] = 0;
            target[type] = 0;
            exits[type] = 0;
            tripSeconds[type] = 0f;
            trips[type] = 0;
            templatesByType[type] = new List<VehicleInfo>();
        }
    }

    private void OnEnable()
    {
        if (trafficAPI == null)
            trafficAPI = FindFirstObjectByType<TrafficAPIManager>();

        if (trafficAPI != null)
        {
            trafficAPI.PredictionUpdated += OnPredictionUpdated;
            trafficAPI.MotorcycleRoutingUpdated += OnMotorcycleRoutingUpdated;
            trafficAPI.RequestFailed += OnRequestFailed;

            // A prediction may have arrived before the spawner was ready.
            if (trafficAPI.HasPrediction)
            {
                OnPredictionUpdated(trafficAPI.CurrentPrediction);
                OnMotorcycleRoutingUpdated(trafficAPI.GroundMotorcycles, trafficAPI.FlyoverMotorcycles);
            }
        }
    }

    private void OnDisable()
    {
        if (trafficAPI != null)
        {
            trafficAPI.PredictionUpdated -= OnPredictionUpdated;
            trafficAPI.MotorcycleRoutingUpdated -= OnMotorcycleRoutingUpdated;
            trafficAPI.RequestFailed -= OnRequestFailed;
        }
    }

    private void Start()
    {
        try
        {
            if (lanes == null || lanes.Length == 0)
                lanes = SceneManager.GetActiveScene().GetRootGameObjects()
                    .SelectMany(root => root.GetComponentsInChildren<JunctionLane>(true))
                    .ToArray();

            lanes = lanes.Where(lane => lane != null && lane.IsValid).ToArray();
            if (lanes.Length == 0)
                Problem("No lanes found. Run Tools > Junction Sim > Set Up Lanes, then save the scene.");

            foreach (VehicleType type in Types)
                templatesByType[type] = (templates ?? new VehicleInfo[0]).Where(t => t != null && t.type == type).ToList();

            foreach (VehicleInfo t in templates ?? new VehicleInfo[0])
            {
                if (t != null && t.gameObject.scene.IsValid())
                    Debug.LogWarning($"JunctionSpawner: template '{t.name}' is an object in the scene, not a prefab. " +
                                     "Drag the prefabs from Assets/JunctionSim/Prefabs (Project window) into Templates instead.", t);
            }

            foreach (VehicleType type in Types)
            {
                if (templatesByType[type].Count == 0)
                    Debug.LogWarning($"JunctionSpawner: no {type} template assigned, so no {type}s can be spawned.");
                if (!lanes.Any(lane => lane.Allows(type)))
                    Debug.LogWarning($"JunctionSpawner: no lane allows {type}s.");
                GetGroup(type);
            }

            if (switchOffOldVehicles)
                SwitchOffOldVehicles();
        }
        catch (Exception exception)
        {
            Problem($"Start-up failed: {exception.GetType().Name}: {exception.Message}");
            Debug.LogException(exception, this);
        }

        if (lanes == null)
            lanes = new JunctionLane[0];
        appliedSource = source;
        countsDirty = true;
        fillAnywhere = true;
    }

    /// <summary>
    /// The parent object for one vehicle type, created on first use. All of them sit under a
    /// top-level JunctionSim_Vehicles object, so the spawner's own position, rotation or scale
    /// (or whatever object it sits inside) can never move, turn or squash the vehicles.
    /// </summary>
    private Transform GetGroup(VehicleType type)
    {
        if (container == null)
            container = new GameObject("JunctionSim_Vehicles").transform;

        if (!groups.TryGetValue(type, out Transform group) || group == null)
        {
            group = new GameObject(GroupName(type)).transform;
            group.SetParent(container, false);
            groups[type] = group;
        }

        return group;
    }

    /// <summary>A problem that stops vehicles appearing: logged once and shown in red in the readout.</summary>
    private void Problem(string message)
    {
        if (problems.Contains(message))
            return;
        problems.Add(message);
        Debug.LogError("JunctionSpawner: " + message, this);
    }

    private void SwitchOffOldVehicles()
    {
        int count = 0;
        foreach (JunctionLane lane in lanes)
        {
            foreach (LaneCarController vehicle in lane.Lane.Vehicles.ToList())
            {
                if (spawnedByMe.Contains(vehicle))
                    continue;
                vehicle.gameObject.SetActive(false);
                count++;
            }
        }

        if (count > 0)
            Debug.Log($"JunctionSpawner: switched off {count} hand-placed vehicle(s) on the lanes (Play mode only; the scene is not changed).");
    }

    // ==========================================================================================
    // Every frame
    // ==========================================================================================

    private void Update()
    {
        HandleKeys();

        if (source != appliedSource)
        {
            appliedSource = source;
            countsDirty = true;
        }

        if (source == CountSource.TestNumbers && TestNumbersChanged())
            countsDirty = true;

        if (countsDirty)
        {
            countsDirty = false;
            RecalculateTargets();
        }

        if (Counting)
            hourElapsed = Mathf.Min(SecondsPerHour, hourElapsed + Time.deltaTime);

        if (Time.time >= nextAttempt)
        {
            nextAttempt = Time.time + Mathf.Max(0.02f, retryInterval);
            Reconcile();
        }
    }

    private void HandleKeys()
    {
#if ENABLE_LEGACY_INPUT_MANAGER
        if (Input.GetKeyDown(toggleReadoutKey))
            showReadout = !showReadout;

        if (Input.GetKeyDown(resetCounterKey))
            ResetPassed();

        KeyCode[] presetKeys = { KeyCode.Alpha1, KeyCode.Alpha2, KeyCode.Alpha3, KeyCode.Alpha4, KeyCode.Alpha5 };
        for (int i = 0; i < presetKeys.Length && presets != null && i < presets.Length; i++)
        {
            if (Input.GetKeyDown(presetKeys[i]))
                LoadPreset(i);
        }
#endif
    }

    /// <summary>Copy a preset into the test numbers and switch to test numbers.</summary>
    public void LoadPreset(int index)
    {
        if (presets == null || index < 0 || index >= presets.Length)
            return;

        TestPreset preset = presets[index];
        testCars = preset.cars;
        testBikes = preset.bikes;
        testBuses = preset.buses;
        testTrucks = preset.trucks;
        source = CountSource.TestNumbers;

        Match hour = HourInName.Match(preset.name ?? "");   // "Low (Fri 3 AM)" -> 3
        if (hour.Success)
        {
            testHour = int.Parse(hour.Groups[1].Value) % 12;
            if (hour.Groups[2].Value.ToUpperInvariant() == "PM")
                testHour += 12;
        }

        Debug.Log($"JunctionSpawner: test preset {index + 1}, {preset.name}.");
    }

    private bool TestNumbersChanged()
    {
        int[] now = { testCars, testBikes, testBuses, testTrucks, testHour };
        if (now.SequenceEqual(appliedTestNumbers))
            return false;
        appliedTestNumbers = now;
        return true;
    }

    // ==========================================================================================
    // Live prediction (TrafficAPIManager)
    // ==========================================================================================

    private int liveCars, liveBuses, liveTrucks, liveGroundBikes, liveFlyoverBikes, liveAllBikes, liveHour = 8;
    private bool liveFlyoverOn;
    private string liveLabel = "waiting for Flask";

    private void OnPredictionUpdated(TrafficAPIManager.TrafficPrediction prediction)
    {
        liveCars = prediction.cars;
        liveBuses = prediction.buses;
        liveTrucks = prediction.trucks;
        liveAllBikes = prediction.motorcycles;
        liveHour = prediction.hour;
        liveLabel = $"{prediction.road}, {TrafficAPIManager.DayName(prediction.day_num)} {prediction.hour:00}:00, {prediction.model}";
        lastError = null;
        if (source == CountSource.LivePrediction)
            countsDirty = true;
    }

    private void OnMotorcycleRoutingUpdated(int groundMotorcycles, int flyoverMotorcycles)
    {
        liveGroundBikes = groundMotorcycles;
        liveFlyoverBikes = flyoverMotorcycles;
        liveFlyoverOn = trafficAPI != null && trafficAPI.FlyoverOn;
        if (source == CountSource.LivePrediction)
            countsDirty = true;
    }

    private void OnRequestFailed(string message)
    {
        lastError = message;   // keep the current traffic; just show the error
    }

    // ==========================================================================================
    // Counts -> targets
    // ==========================================================================================

    private void RecalculateTargets()
    {
        var oldPredicted = Types.Select(t => Predicted(t)).ToArray();
        int oldFlyover = predictedFlyoverBikes;
        int oldHour = hourStart;

        if (source == CountSource.LivePrediction)
        {
            predicted[VehicleType.Car] = liveCars;
            predicted[VehicleType.Bike] = liveGroundBikes;   // ground bikes only
            predicted[VehicleType.Bus] = liveBuses;
            predicted[VehicleType.Truck] = liveTrucks;
            predictedFlyoverBikes = liveFlyoverBikes;
            flyoverOn = liveFlyoverOn;
            sourceLabel = "Live: " + liveLabel;
            hourStart = ((liveHour % 24) + 24) % 24;
        }
        else
        {
            predicted[VehicleType.Car] = Mathf.Max(0, testCars);
            predicted[VehicleType.Bike] = Mathf.Max(0, testBikes);
            predicted[VehicleType.Bus] = Mathf.Max(0, testBuses);
            predicted[VehicleType.Truck] = Mathf.Max(0, testTrucks);
            predictedFlyoverBikes = 0;
            flyoverOn = false;
            sourceLabel = "Test numbers";
            hourStart = ((testHour % 24) + 24) % 24;
        }

        float ratio = Mathf.Max(1f, vehiclesPerHourPerSpawned);
        var raw = new Dictionary<VehicleType, int>();
        foreach (VehicleType type in Types)
            raw[type] = Mathf.RoundToInt(predicted[type] / ratio);

        Dictionary<VehicleType, int> next = ApplyCap(raw, Mathf.Max(0, maxVehiclesOnRoad));

        bool changed = Types.Any(type => next[type] != target[type]);
        bool forecastChanged = predictedFlyoverBikes != oldFlyover ||
                               Types.Select((type, i) => predicted[type] != oldPredicted[i]).Any(x => x);
        bool rose = Types.Any(type => next[type] > target[type]);
        foreach (VehicleType type in Types)
            target[type] = next[type];

        if (forecastChanged)
            ResetCrossingTimes();   // different traffic, different crossing times
        if (changed || forecastChanged || hourStart != oldHour)
            ResetPassed();          // new hour / new model / flyover toggled: start counting again

        if (!changed)
            return;

        if (rose && fillInstantlyWhenTargetsRise)
            fillAnywhere = true;

        string summary = $"JunctionSpawner: {sourceLabel}. Targets on screen (1 per {ratio:0} per hour): " +
                         string.Join(", ", Types.Select(t => $"{GroupName(t).ToLower()} {target[t]} (from {predicted[t]})"));
        if (capNote.Length > 0)
            Debug.LogWarning(summary + ". " + capNote + ".");
        else
            Debug.Log(summary);
        nextAttempt = 0f;
        CountsChanged?.Invoke();
    }

    /// <summary>If the targets add up to more than the cap, scale all types down by the same factor (largest-remainder rounding).</summary>
    private Dictionary<VehicleType, int> ApplyCap(Dictionary<VehicleType, int> raw, int cap)
    {
        int total = raw.Values.Sum();
        capNote = "";
        if (total <= cap)
            return raw;

        float factor = (float)cap / total;
        var result = new Dictionary<VehicleType, int>();
        var remainders = new List<KeyValuePair<VehicleType, float>>();
        foreach (VehicleType type in Types)
        {
            float exact = raw[type] * factor;
            result[type] = Mathf.FloorToInt(exact);
            remainders.Add(new KeyValuePair<VehicleType, float>(type, exact - result[type]));
        }

        int left = cap - result.Values.Sum();
        foreach (var pair in remainders.OrderByDescending(p => p.Value).Take(left))
            result[pair.Key]++;

        capNote = $"Capped: {total} would not fit, showing {cap} (every type x {factor:0.00})";
        return result;
    }

    // ==========================================================================================
    // Keep the road matching the targets
    // ==========================================================================================

    private void Reconcile()
    {
        if (lanes == null || lanes.Length == 0)
            return;

        // Vehicles deleted by something else (e.g. by hand in the Hierarchy) are forgotten.
        foreach (VehicleType type in Types)
            active[type].RemoveAll(v => v == null);
        leaving.RemoveWhere(v => v == null);
        spawnedByMe.RemoveWhere(v => v == null);
        foreach (LaneCarController gone in enteredAt.Keys.Where(v => v == null).ToList())
            enteredAt.Remove(gone);

        // 1. Too many of a type: un-needed vehicles leave (or vanish if Remove Extras Instantly).
        if (removeExtrasInstantly)
        {
            foreach (LaneCarController v in leaving.ToList())
            {
                Forget(v);
                Destroy(v.gameObject);
            }
        }

        foreach (VehicleType type in Types)
        {
            List<LaneCarController> list = active[type];
            int staying = list.Count(v => !leaving.Contains(v));
            int extra = staying - target[type];

            if (extra > 0)
            {
                // Those closest to the end leave first, so the road settles quickly.
                foreach (LaneCarController v in list.Where(v => !leaving.Contains(v))
                                                     .OrderByDescending(ProgressAlongLane).Take(extra).ToList())
                {
                    if (removeExtrasInstantly)
                    {
                        Forget(v);
                        Destroy(v.gameObject);
                    }
                    else
                    {
                        leaving.Add(v);
                    }
                }
            }
            else if (extra < 0)
            {
                // Too few: first keep vehicles that were about to leave.
                foreach (LaneCarController v in list.Where(v => leaving.Contains(v)).Take(-extra).ToList())
                    leaving.Remove(v);
            }
        }

        // 2. Too few: place new vehicles, shuffled so types are mixed.
        var needed = new List<VehicleType>();
        foreach (VehicleType type in Types)
            for (int i = 0; i < Waiting(type); i++)
                needed.Add(type);

        Shuffle(needed);
        bool anywhere = fillAnywhere;
        foreach (VehicleType type in needed)
        {
            try
            {
                TrySpawn(type, anywhere);
            }
            catch (Exception exception)
            {
                if (!problems.Any(p => p.StartsWith("Could not place")))
                    Debug.LogException(exception, this);
                Problem($"Could not place vehicles: {exception.GetType().Name}: {exception.Message}");
                break;
            }
        }

        fillAnywhere = false;
    }

    private bool TrySpawn(VehicleType type, bool anywhere)
    {
        if (!templatesByType.TryGetValue(type, out List<VehicleInfo> choices) || choices.Count == 0)
            return false;

        VehicleInfo template = choices[UnityEngine.Random.Range(0, choices.Count)];
        float half = template.HalfLength;
        float gap = template.safetyGap;

        foreach (JunctionLane lane in LanesFor(type))
        {
            if (anywhere)
            {
                if (TryPlaceInAnyGap(lane, template, half, gap))
                    return true;
            }
            else if (TryPlace(lane, template, half + 0.05f, half, gap, true))
            {
                return true;   // entered at the start of the lane
            }
        }

        return false;   // no room right now; it stays "waiting to enter" and is tried again shortly
    }

    /// <summary>
    /// Works out every stretch of the lane where this vehicle fits (keeping everyone's safety gaps,
    /// staying out of the crossing and away from the ends) and places it at a random spot in one.
    /// </summary>
    private bool TryPlaceInAnyGap(JunctionLane lane, VehicleInfo template, float half, float gap)
    {
        float low = half;
        float high = lane.Length - lane.endMargin - half;
        if (high <= low)
            return false;

        // Ranges of centre positions that are NOT allowed.
        var blocked = new List<Vector2>();
        float stop = lane.StopLineDistance;
        blocked.Add(new Vector2(stop - half, stop + lane.crossingLength + half));
        foreach (LaneCarController other in lane.Lane.Vehicles)
        {
            float at = lane.DistanceAlong(other.transform.position);
            blocked.Add(new Vector2(at - half - other.HalfLength - gap, at + half + other.HalfLength + other.SafetyGap));
        }
        blocked.Sort((a, b) => a.x.CompareTo(b.x));

        // Free ranges = [low, high] minus the blocked ranges.
        var free = new List<Vector2>();
        float cursor = low;
        foreach (Vector2 range in blocked)
        {
            if (range.y <= cursor)
                continue;
            if (range.x > cursor)
                free.Add(new Vector2(cursor, Mathf.Min(range.x, high)));
            cursor = Mathf.Max(cursor, range.y);
            if (cursor >= high)
                break;
        }
        if (cursor < high)
            free.Add(new Vector2(cursor, high));
        free.RemoveAll(range => range.y - range.x < 0.02f);

        if (free.Count == 0)
            return false;

        // Pick a random spot, bigger gaps being more likely.
        float total = free.Sum(range => range.y - range.x);
        float pick = UnityEngine.Random.Range(0f, total);
        foreach (Vector2 range in free)
        {
            float length = range.y - range.x;
            if (pick <= length)
                return TryPlace(lane, template, range.x + Mathf.Clamp(pick, 0.01f, length - 0.01f), half, gap, false);
            pick -= length;
        }

        return false;
    }

    /// <param name="atStart">True if the vehicle enters at the start of the road, so its whole trip can be timed.</param>
    private bool TryPlace(JunctionLane lane, VehicleInfo template, float distance, float half, float gap, bool atStart)
    {
        if (lane.IsNoSpawn(distance, half))
            return false;

        Vector3 position = lane.PointAt(distance);
        if (!lane.Lane.IsClear(position, lane.Direction, half, gap))
            return false;

        VehicleType type = template.type;
        GameObject vehicle = Instantiate(template.gameObject, position, lane.Rotation, GetGroup(type));
        vehicle.name = $"{template.name}_{++spawnCounter:000}";
        vehicle.SetActive(true);   // a copy of a switched-off template would otherwise be switched off too

        var driver = vehicle.GetComponent<LaneCarController>();
        if (driver == null)
        {
            WarnOnce(template.name, $"JunctionSpawner: template {template.name} has no LaneCarController; removed.");
            Destroy(vehicle);
            return false;
        }

        driver.Setup(lane.Lane, lane.stopLine, lane.signal, lane.endPoint, template.PickSpeed());
        driver.ReachedEnd += OnVehicleReachedEnd;
        active[type].Add(driver);
        spawnedByMe.Add(driver);
        if (atStart)
            enteredAt[driver] = Time.time;

        if (spawnCounter == 1)
            LogFirstVehicle(vehicle, lane);
        return true;
    }

    /// <summary>One-time check that the first vehicle really is on the road, upright and full size.</summary>
    private static void LogFirstVehicle(GameObject vehicle, JunctionLane lane)
    {
        Renderer[] renderers = vehicle.GetComponentsInChildren<Renderer>();
        Bounds box = renderers.Length > 0 ? renderers[0].bounds : new Bounds(vehicle.transform.position, Vector3.zero);
        foreach (Renderer r in renderers)
            box.Encapsulate(r.bounds);

        Debug.Log($"JunctionSpawner: first vehicle {vehicle.name} in {lane.name} at {vehicle.transform.position}, " +
                  $"scale {vehicle.transform.lossyScale}, active {vehicle.activeInHierarchy}, {renderers.Length} renderers, " +
                  $"visible size {box.size} (a car should be about 1.1 long, 0.5 wide, 0.4 high).", vehicle);
    }

    /// <summary>Lanes that allow this type, in the order to try them: preferred lanes first for buses/trucks, otherwise random.</summary>
    private IEnumerable<JunctionLane> LanesFor(VehicleType type)
    {
        List<JunctionLane> allowed = lanes.Where(lane => lane.Allows(type)).ToList();
        Shuffle(allowed);

        if (type == VehicleType.Bus || type == VehicleType.Truck)
            return allowed.OrderByDescending(lane => lane.heavyVehiclesPreferred);

        return allowed;
    }

    private void OnVehicleReachedEnd(LaneCarController vehicle)
    {
        // Extra vehicles from earlier traffic that are driving off (Leaving) are not counted:
        // the counter started again when the traffic changed.
        if (vehicle.Info != null && Counting && !leaving.Contains(vehicle))
        {
            VehicleType type = vehicle.Info.type;
            exits[type]++;

            // Whole trips (entered at the start) give the measured crossing time.
            if (enteredAt.TryGetValue(vehicle, out float entered))
            {
                tripSeconds[type] += Time.time - entered;
                trips[type]++;
            }

            PassedChanged?.Invoke();
        }

        Forget(vehicle);   // the next Reconcile places a replacement if this type is short
    }

    private void Forget(LaneCarController vehicle)
    {
        vehicle.ReachedEnd -= OnVehicleReachedEnd;
        leaving.Remove(vehicle);
        spawnedByMe.Remove(vehicle);
        enteredAt.Remove(vehicle);
        foreach (VehicleType type in Types)
            active[type].Remove(vehicle);
    }

    private static float ProgressAlongLane(LaneCarController vehicle)
    {
        return vehicle.endPoint == null
            ? 0f
            : -Vector3.Dot(vehicle.endPoint.position - vehicle.transform.position, vehicle.transform.forward);
    }

    private static void Shuffle<T>(IList<T> list)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = UnityEngine.Random.Range(0, i + 1);
            T temp = list[i];
            list[i] = list[j];
            list[j] = temp;
        }
    }

    private static string GroupName(VehicleType type)
    {
        switch (type)
        {
            case VehicleType.Car: return "Cars";
            case VehicleType.Bike: return "Bikes";
            case VehicleType.Bus: return "Buses";
            default: return "Trucks";
        }
    }

    private void WarnOnce(string key, string message)
    {
        if (warnedOnce.Add(key))
            Debug.LogWarning(message);
    }

    // ==========================================================================================
    // Readout (testing aid; Adithya's UI panel is the real display)
    // ==========================================================================================

    private void OnGUI()
    {
        if (!showReadout)
            return;

        if (readoutStyle == null)
        {
            readoutStyle = new GUIStyle(GUI.skin.label) { fontSize = 13, richText = true, wordWrap = true };
            readoutStyle.normal.textColor = Color.white;
        }

        GUILayout.BeginArea(new Rect(10f, 10f, 530f, Screen.height - 20f));
        GUILayout.BeginVertical(GUI.skin.box);
        GUILayout.Label("<b>Junction spawner</b>   " + sourceLabel +
                        $"\n1 vehicle on screen = {vehiclesPerHourPerSpawned:0} per hour   |   1 hour = {HourLengthText()}", readoutStyle);

        // The hour counter (what the panel shows), then the spawner's own table.
        GUILayout.Label(ClockLine(), readoutStyle);
        DrawProgressBar(HourProgress);
        GUILayout.Label(CounterText(), readoutStyle);
        if (GUILayout.Button($"Reset counter ({resetCounterKey})"))
            ResetPassed();
        GUILayout.Space(4f);

        Row("<b>Type</b>", "<b>Predicted</b>", "<b>Target</b>", "<b>On road</b>", "<b>Waiting*</b>", "<b>Leaving</b>");
        foreach (VehicleType type in Types)
            Row(type == VehicleType.Bike ? "Bikes*" : GroupName(type), Predicted(type).ToString(), Target(type).ToString(),
                OnRoad(type).ToString(), Waiting(type).ToString(), Leaving(type).ToString());
        Row("<b>Total</b>", Types.Sum(Predicted).ToString(), Types.Sum(Target).ToString(), Types.Sum(OnRoad).ToString(),
            Types.Sum(Waiting).ToString(), Types.Sum(Leaving).ToString());

        GUILayout.Label(ReadoutFooter(), readoutStyle);
        GUILayout.EndVertical();
        GUILayout.EndArea();
    }

    private void Row(params string[] cells)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label(cells[0], readoutStyle, GUILayout.Width(70f));
        for (int i = 1; i < cells.Length; i++)
            GUILayout.Label(cells[i], readoutStyle, GUILayout.Width(i == 1 ? 80f : 62f));
        GUILayout.EndHorizontal();
    }

    private static void DrawProgressBar(float fraction)
    {
        Rect bar = GUILayoutUtility.GetRect(300f, 8f, GUILayout.ExpandWidth(true));
        Color old = GUI.color;
        GUI.color = new Color(1f, 1f, 1f, 0.2f);
        GUI.DrawTexture(bar, Texture2D.whiteTexture);
        GUI.color = new Color(0.42f, 1f, 0.56f);
        GUI.DrawTexture(new Rect(bar.x, bar.y, bar.width * Mathf.Clamp01(fraction), bar.height), Texture2D.whiteTexture);
        GUI.color = old;
    }

    /// <summary>The readout as text (also used by the tests).</summary>
    public string BuildReadout()
    {
        var text = new StringBuilder();
        text.AppendLine("<b>Junction spawner</b>   " + sourceLabel);
        text.AppendLine($"1 vehicle on screen = {vehiclesPerHourPerSpawned:0} per hour   |   1 hour = {HourLengthText()}");
        int filled = Mathf.RoundToInt(HourProgress * 10f);
        text.AppendLine(ClockLine() + "   [" + new string('#', filled) + new string('-', 10 - filled) + "]");
        text.AppendLine(CounterText());
        text.AppendLine();
        text.AppendLine("<b>Type        Predicted   Target   On road   Waiting*  Leaving</b>");
        foreach (VehicleType type in Types)
        {
            string label = type == VehicleType.Bike ? "Bikes*" : GroupName(type);
            text.AppendLine($"{label,-10}  {Predicted(type),9}   {Target(type),6}   {OnRoad(type),7}   {Waiting(type),7}   {Leaving(type),7}");
        }

        int targetTotal = Types.Sum(Target);
        int onRoadTotal = Types.Sum(OnRoad);
        text.AppendLine($"{"Total",-10}  {Types.Sum(Predicted),9}   {targetTotal,6}   {onRoadTotal,7}   {Types.Sum(Waiting),7}   {Types.Sum(Leaving),7}");
        text.Append(ReadoutFooter());
        return text.ToString();
    }

    private string HourLengthText()
    {
        int seconds = Mathf.RoundToInt(SecondsPerHour);
        return $"{seconds / 60}:{seconds % 60:00} min";
    }

    private string ClockLine()
    {
        if (ForecastTotal <= 0)
            return "<b>Clock --:--</b>   waiting for traffic numbers";
        if (HourComplete)
            return $"<b>Clock {ClockText}</b>   (hour complete)";
        return $"<b>Clock {ClockText}</b>   (hour {HourProgress * 100f:0}% done)";
    }

    /// <summary>A whole number with thousands separators, e.g. 20,792.</summary>
    private static string N(int value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private static string MatchColour(float percent)
    {
        return percent >= 95f && percent <= 105f ? "#6bff8f" : percent >= 85f && percent <= 115f ? "#ffcc66" : "#ff6b6b";
    }

    /// <summary>The hour counter lines: exact exits, counted, projection and match, flyover, worth per exit.</summary>
    private string CounterText()
    {
        var text = new StringBuilder();
        bool showFlyover = flyoverOn && predictedFlyoverBikes > 0;

        text.AppendLine("<b>Reached end points (actual):</b>   " +
                        string.Join(" | ", Types.Select(t => $"{GroupName(t)} {ReachedEnd(t)}")) + $"  = {ReachedEndTotal}" +
                        (showFlyover ? $"   (flyover laps {flyoverLaps})" : ""));
        text.AppendLine($"<b>Counted for the hour:</b>   {N(CountedTotal)} / {N(ForecastTotal)}");

        int projected = ProjectedHourTotal;
        if (HourComplete)
        {
            text.AppendLine($"<b>Hour complete ({HourLabel}):</b> counted {N(CountedTotal)} of {N(ForecastTotal)}   " +
                            $"<color={MatchColour(MatchPercent)}>({MatchPercent:0.0}% match)</color>   " +
                            $"- press {resetCounterKey} or Reset to run it again");
        }
        else if (projected >= 0)
        {
            text.AppendLine($"<b>At this rate, 1 hour = {N(projected)}</b>   " +
                            $"<color={MatchColour(MatchPercent)}>(dashboard {N(ForecastTotal)}, {MatchPercent:0.0}% match)</color>");
        }
        else if (ForecastTotal > 0)
        {
            text.AppendLine("<b>At this rate, 1 hour =</b> ... (shown after the first 6 minutes)");
        }

        if (showFlyover)
        {
            text.AppendLine($"Before flyover {N(ForecastTotal)}  ->  After {N(ForecastGround)}   Bikes diverted {N(predictedFlyoverBikes)}");
            text.AppendLine($"Ground counted {N(CountedGround)} / {N(ForecastGround)}   |   Flyover counted {N(CountedFlyover)} / {N(predictedFlyoverBikes)}");
            if (flyoverLaps == 0 && HourProgress >= 0.1f)
                text.AppendLine("<color=#ffcc66>No flyover lap counted yet: is FlyoverBikeSimulation in the scene with its route points and bike prefab?</color>");
        }

        // How much one exit is worth, so the counted number can be checked by hand.
        text.Append($"<i>1 exit = {vehiclesPerHourPerSpawned:0} x crossing time / {SecondsPerHour:0} s:  " +
                    string.Join(" | ", Types.Select(t => $"{GroupName(t).ToLower()} {AverageCrossingSeconds(t):0.0} s{(CrossingTimeMeasured(t) ? "" : " (est.)")} = {WorthPerExit(t):0}")) +
                    (showFlyover ? $" | flyover lap {AverageFlyoverLapSeconds:0.0} s = {WorthPerFlyoverLap:0}" : "") + "</i>");
        return text.ToString();
    }

    private string ReadoutFooter()
    {
        var text = new StringBuilder();
        foreach (string problem in problems)
            text.AppendLine("<color=#ff6b6b><b>Problem:</b> " + problem + "</color>");
        foreach (VehicleType type in Types)
        {
            if (Target(type) > 0 && templatesByType.TryGetValue(type, out var list) && list.Count == 0)
                text.AppendLine($"<color=#ff6b6b><b>Problem:</b> no {GroupName(type)} templates. Drag all prefabs from " +
                                "Assets/JunctionSim/Prefabs into Templates.</color>");
        }
        text.AppendLine("Waiting* = queued to enter because the start of the road is full. On road + waiting = target.");
        text.AppendLine("Bikes* = ground bikes only" + (source == CountSource.LivePrediction
            ? $"; all bikes {PredictedAllBikes}/hour, flyover {(flyoverOn ? "ON" : "OFF")} takes {predictedFlyoverBikes}"
            : ""));

        if (capNote.Length > 0)
            text.AppendLine("<color=#ffcc66>" + capNote + "</color>");
        if (!string.IsNullOrEmpty(lastError) && source == CountSource.LivePrediction)
            text.AppendLine("<color=#ff6b6b>Flask error, keeping current traffic: </color>" + lastError);

        bool settled = Types.All(t => Waiting(t) == 0 && Leaving(t) == 0 && OnRoad(t) == Target(t));
        bool balanced = Types.All(t => OnRoad(t) - Leaving(t) + Waiting(t) == Target(t));
        text.AppendLine(settled ? "<color=#6bff8f>On road = target for every type</color>"
                        : balanced && Types.All(t => Leaving(t) == 0) ? "<color=#ffcc66>Some vehicles queued to enter</color>"
                        : "Settling...");
        text.Append($"<i>Keys: 1-5 test presets | {resetCounterKey} reset counter | G hide this box</i>");
        return text.ToString();
    }
}