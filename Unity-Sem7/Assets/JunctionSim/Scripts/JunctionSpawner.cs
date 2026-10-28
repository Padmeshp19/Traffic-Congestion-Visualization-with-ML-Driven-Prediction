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
/// HOURLY FLOW (default): one predicted hour is played in a few minutes and the vehicles are counted.
///   - Vehicles arrive at the start at the predicted rate. Each vehicle on screen stands for 100 real ones,
///     so 7104 cars per hour = 71 cars entering during the simulated hour.
///   - Every vehicle that reaches the end point is counted as 100 real vehicles.
///   - A clock runs from the predicted hour (08:00) to the next one (09:00). By then the count should have
///     reached about the dashboard total; "At this rate, 1 hour = ..." and the match % show how close it is.
///   - When the traffic changes (new prediction, flyover switched, other test numbers) a fresh hour starts.
///     Vehicles beyond the new traffic are taken off the road, the road is filled about as busy as the
///     new traffic makes it, and the clock starts 5 minutes early (07:55) so the road settles; counting
///     starts at 08:00, like a real road that is already busy when the hour begins.
///   - When an hour ends, its result is kept on screen and the next hour starts with the same traffic.
///   - If vehicles arrive faster than the road can take them in, they wait at the start (congestion) and
///     the count falls behind the prediction. Nothing is faked to make the numbers match.
///
/// KEEP ON SCREEN (the earlier mode): predicted per hour / 600 = a fixed number of each type kept on the road.
///
/// Counts come from typed-in test numbers (keys 1-5 pick presets) or from TrafficAPIManager.
/// Motorcycles use the GROUND count only. The flyover simulation releases its bikes at
/// FlyoverBikesPerSecond and calls RecordFlyoverBikeFinished() for each bike that reaches the end.
/// </summary>
public class JunctionSpawner : MonoBehaviour
{
    public enum CountSource
    {
        TestNumbers,
        LivePrediction
    }

    public enum SpawnMode
    {
        HourlyFlow,
        KeepOnScreen
    }

    [Serializable]
    public class TestPreset
    {
        public string name;
        public int cars, bikes, buses, trucks;
    }

    /// <summary>The result of one simulated hour.</summary>
    [Serializable]
    public class HourResult
    {
        public string label;           // where the traffic came from, e.g. "Live: Silk Board Junction, Friday 08:00, gbm"
        public string clock;           // e.g. "08:00-09:00"
        public int predicted;          // dashboard total for the hour (before the flyover)
        public int counted;            // counted on the ground road + on the flyover (real vehicles)
        public int countedGround;
        public int countedFlyover;
        public bool groundOnly;        // true if no flyover bike was counted, so only the ground road was compared
        public int comparedWith;       // the predicted number the count was compared with
        public float matchPercent;     // count / comparedWith x 100
        public int[] reachedEnd = new int[4];   // vehicles on screen that reached the end: cars, bikes, buses, trucks
    }

    [Header("Where vehicles drive (leave empty to use every JunctionLane in the scene)")]
    public JunctionLane[] lanes;

    [Header("Vehicle templates (drag all of Assets/JunctionSim/Prefabs here)")]
    public VehicleInfo[] templates;

    [Header("Mode")]
    [Tooltip("Hourly Flow: vehicles arrive at the predicted rate and are counted at the end of the road; one hour is played in a few minutes. " +
             "Keep On Screen: the earlier mode that keeps a fixed number on the road.")]
    public SpawnMode mode = SpawnMode.HourlyFlow;

    [Header("Hourly Flow")]
    [Tooltip("Each vehicle on screen stands for this many real vehicles.")]
    public int realVehiclesPerVehicle = 100;
    [Tooltip("Real seconds that one simulated hour takes (180 = 3 minutes). Shorter hours mean more vehicles per second; " +
             "below about 150 s the busiest hours jam.")]
    public float secondsPerSimulatedHour = 180f;
    [Tooltip("When the traffic changes, fill the road about as busy as the new traffic makes it, instead of starting empty.")]
    public bool prefillRoad = true;
    [Tooltip("When the traffic changes, vehicles beyond the new traffic vanish at once (most accurate count). " +
             "Untick to let them drive off instead (not counted); the old traffic then slows the first hour a little.")]
    public bool clearOldTrafficAtOnce = true;
    [Tooltip("Simulated minutes the clock runs before the hour starts (07:55 -> 08:00), so the road settles with the new traffic before counting.")]
    public float settleMinutes = 5f;
    [Tooltip("Hour shown on the clock for test numbers. Keys 1-5 set it from the preset name.")]
    public int testHour = 8;

    [Header("Keep On Screen")]
    [Tooltip("1 vehicle on screen stands for this many predicted vehicles per hour.")]
    public float vehiclesPerHourPerSpawned = 600f;
    [Tooltip("Upper limit on screen. If the targets add up to more, every type is scaled down by the same factor.")]
    public int maxVehiclesOnRoad = 70;

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
    [Tooltip("Keep On Screen: when targets go up, place the new vehicles anywhere there is room, so the road fills at once.")]
    public bool fillInstantlyWhenTargetsRise = true;
    [Tooltip("Remove extra / left-over vehicles at once instead of letting them drive to the end (handy for testing).")]
    public bool removeExtrasInstantly;
    [Tooltip("Keep On Screen: seconds between attempts to place vehicles that are waiting for room.")]
    public float retryInterval = 0.2f;
    [Tooltip("Switch off the hand-placed vehicles already on these lanes while in Play mode, so they do not spoil the counts.")]
    public bool switchOffOldVehicles = true;

    [Header("Readout")]
    public bool showReadout = true;
    public KeyCode toggleReadoutKey = KeyCode.G;

    private static readonly VehicleType[] Types = { VehicleType.Car, VehicleType.Bike, VehicleType.Bus, VehicleType.Truck };
    private struct Arrival
    {
        public VehicleType type;
        public VehicleInfo template;
    }

    private static readonly Regex HourInName = new Regex(@"(\d{1,2})\s*(AM|PM)", RegexOptions.IgnoreCase);

    private readonly Dictionary<VehicleType, List<LaneCarController>> active = new Dictionary<VehicleType, List<LaneCarController>>();
    private readonly HashSet<LaneCarController> leaving = new HashSet<LaneCarController>();
    private readonly HashSet<LaneCarController> spawnedByMe = new HashSet<LaneCarController>();
    private readonly Dictionary<VehicleType, int> predicted = new Dictionary<VehicleType, int>();
    private readonly Dictionary<VehicleType, int> target = new Dictionary<VehicleType, int>();
    private readonly Dictionary<VehicleType, Transform> groups = new Dictionary<VehicleType, Transform>();
    private readonly Dictionary<VehicleType, List<VehicleInfo>> templatesByType = new Dictionary<VehicleType, List<VehicleInfo>>();

    // Hourly Flow
    private readonly Dictionary<VehicleType, float> owed = new Dictionary<VehicleType, float>();          // arrivals building up, in vehicles
    private readonly Dictionary<VehicleType, float> nextArrival = new Dictionary<VehicleType, float>();   // "owed" must reach this for the next arrival
    private readonly Dictionary<VehicleType, int> queued = new Dictionary<VehicleType, int>();            // arrived, waiting for room at the start
    private readonly List<Arrival> arrivals = new List<Arrival>();                                       // the same vehicles, in the order they arrived
    private readonly Dictionary<VehicleType, int> reachedEnd = new Dictionary<VehicleType, int>();        // this hour
    private readonly Dictionary<VehicleType, int> enteredThisHour = new Dictionary<VehicleType, int>();
    private string trafficKey;
    private float hourElapsed;
    private int hourStart;
    private int flyoverFinished;
    private bool flyoverBikesReported;

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
    private SpawnMode appliedMode;
    private int[] appliedTestNumbers = new int[5];
    private readonly HashSet<string> warnedOnce = new HashSet<string>();
    private readonly List<string> problems = new List<string>();
    private Transform container;
    private GUIStyle readoutStyle;

    /// <summary>Sent whenever the traffic numbers change (useful for the UI panel).</summary>
    public event Action CountsChanged;
    /// <summary>Hourly Flow: sent when a simulated hour starts (fresh traffic, or the next hour of the same traffic).</summary>
    public event Action HourStarted;
    /// <summary>Hourly Flow: sent when the clock reaches the end of the hour, with that hour's result.</summary>
    public event Action<HourResult> HourFinished;

    // ---- Numbers for the UI panel -------------------------------------------------------------
    /// <summary>Predicted vehicles per hour for this type (bikes: ground bikes only).</summary>
    public int Predicted(VehicleType type) => predicted.TryGetValue(type, out int n) ? n : 0;
    /// <summary>Keep On Screen: the number kept on the road. Hourly Flow: about how many are on the road with this traffic.</summary>
    public int Target(VehicleType type) => target.TryGetValue(type, out int n) ? n : 0;
    /// <summary>Vehicles of this type on the road right now (including any left-overs driving off).</summary>
    public int OnRoad(VehicleType type) => active.TryGetValue(type, out var list) ? list.Count : 0;
    /// <summary>Vehicles driving off that are not part of the current traffic (not counted).</summary>
    public int Leaving(VehicleType type) => active.TryGetValue(type, out var list) ? list.Count(v => leaving.Contains(v)) : 0;
    /// <summary>Vehicles waiting to enter because the start of the road is full.</summary>
    public int Waiting(VehicleType type) => IsHourlyFlow
        ? (queued.TryGetValue(type, out int q) ? q : 0)
        : Mathf.Max(0, Target(type) - (OnRoad(type) - Leaving(type)));
    public int PredictedFlyoverBikes => predictedFlyoverBikes;
    /// <summary>All predicted motorcycles, ground + flyover (same as the dashboard with the flyover OFF).</summary>
    public int PredictedAllBikes => source == CountSource.LivePrediction ? liveAllBikes : Predicted(VehicleType.Bike);
    public bool FlyoverOn => flyoverOn;
    public bool IsCapped => capNote.Length > 0;
    public string SourceLabel => sourceLabel;

    // ---- Hourly Flow: clock and counts --------------------------------------------------------
    public bool IsHourlyFlow => mode == SpawnMode.HourlyFlow;
    public int RealVehiclesPerVehicle => Mathf.Max(1, realVehiclesPerVehicle);
    public float SecondsPerHour => Mathf.Max(10f, secondsPerSimulatedHour);
    /// <summary>True while the hour clock is running (Hourly Flow, with traffic numbers and lanes).</summary>
    public bool ClockRunning => IsHourlyFlow && HasCounts && lanes != null && lanes.Length > 0;
    /// <summary>0 at the start of the simulated hour, 1 at the end (0 while the road is settling).</summary>
    public float HourProgress => Mathf.Clamp01(hourElapsed / SecondsPerHour);
    /// <summary>True during the few minutes before the hour (07:55-08:00) while the road settles; nothing is counted.</summary>
    public bool IsSettling => hourElapsed < 0f;
    public int HourStart => hourStart;
    /// <summary>The simulated time, e.g. "08:34" (or "07:52" while settling).</summary>
    public string ClockText
    {
        get
        {
            int minutes = Mathf.Min(60, Mathf.FloorToInt(hourElapsed / SecondsPerHour * 60f));
            int total = ((hourStart * 60 + minutes) % 1440 + 1440) % 1440;
            return $"{total / 60:00}:{total % 60:00}";
        }
    }
    /// <summary>Vehicles of this type (on screen) that reached the end point this hour.</summary>
    public int ReachedEndThisHour(VehicleType type) => reachedEnd.TryGetValue(type, out int n) ? n : 0;
    /// <summary>Vehicles of this type (on screen) that entered at the start this hour.</summary>
    public int EnteredThisHour(VehicleType type) => enteredThisHour.TryGetValue(type, out int n) ? n : 0;
    /// <summary>Real vehicles counted for this type this hour (reached end x real vehicles per vehicle).</summary>
    public int Counted(VehicleType type) => ReachedEndThisHour(type) * RealVehiclesPerVehicle;
    public int CountedGround => Types.Sum(Counted);
    public int CountedFlyover => flyoverFinished * RealVehiclesPerVehicle;
    public int CountedTotal => CountedGround + CountedFlyover;
    /// <summary>Predicted for the ground road (after the flyover takes its bikes).</summary>
    public int PredictedGround => Types.Sum(Predicted);
    /// <summary>The dashboard total (before the flyover).</summary>
    public int PredictedTotal => PredictedGround + (flyoverOn ? predictedFlyoverBikes : 0);
    /// <summary>True while the flyover is ON but no flyover bike has been counted, so only the ground road is compared.</summary>
    public bool ComparingGroundOnly => flyoverOn && predictedFlyoverBikes > 0 && !flyoverBikesReported;
    public int ComparedWith => ComparingGroundOnly ? PredictedGround : PredictedTotal;
    public int ComparedCount => ComparingGroundOnly ? CountedGround : CountedTotal;
    /// <summary>What a full hour would come to at the rate counted so far; -1 until 10% of the hour has passed.</summary>
    public int ProjectedHourTotal => ClockRunning && !IsSettling && HourProgress >= 0.1f ? Mathf.RoundToInt(ComparedCount / HourProgress) : -1;
    /// <summary>ProjectedHourTotal as a % of the prediction; -1 until 10% of the hour has passed.</summary>
    public float MatchPercent => ProjectedHourTotal < 0 || ComparedWith <= 0 ? -1f : 100f * ProjectedHourTotal / ComparedWith;
    /// <summary>The last full hour, or null if none has finished since the traffic last changed.</summary>
    public HourResult LastHour { get; private set; }
    /// <summary>For the flyover simulation: release flyover bikes at this rate (per real second).</summary>
    public float FlyoverBikesPerSecond => IsHourlyFlow && flyoverOn ? predictedFlyoverBikes / (float)RealVehiclesPerVehicle / SecondsPerHour : 0f;

    /// <summary>For the flyover simulation: call once for every flyover bike that reaches the end of the flyover.</summary>
    public void RecordFlyoverBikeFinished()
    {
        if (!ClockRunning || IsSettling)
            return;
        flyoverFinished++;
        flyoverBikesReported = true;
    }

    private bool HasCounts => source == CountSource.TestNumbers || liveHasPrediction;

    /// <summary>Vehicles of this type arriving per real second.</summary>
    private float PerSecond(VehicleType type) => Predicted(type) / (float)RealVehiclesPerVehicle / SecondsPerHour;

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
            templatesByType[type] = new List<VehicleInfo>();
            owed[type] = 0f;
            nextArrival[type] = 1f;
            queued[type] = 0;
            reachedEnd[type] = 0;
            enteredThisHour[type] = 0;
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
        appliedMode = mode;
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

        if (source != appliedSource || mode != appliedMode)
        {
            appliedSource = source;
            appliedMode = mode;
            countsDirty = true;
        }

        if (source == CountSource.TestNumbers && TestNumbersChanged())
            countsDirty = true;

        if (IsHourlyFlow && trafficKey != null && trafficKey != TrafficKey())
            countsDirty = true;   // e.g. the scale or hour length was changed in the Inspector

        if (countsDirty)
        {
            countsDirty = false;
            RecalculateTargets();
        }

        if (IsHourlyFlow)
        {
            FlowUpdate(Time.deltaTime);
        }
        else if (Time.time >= nextAttempt)
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

        KeyCode[] presetKeys = { KeyCode.Alpha1, KeyCode.Alpha2, KeyCode.Alpha3, KeyCode.Alpha4, KeyCode.Alpha5 };
        for (int i = 0; i < presetKeys.Length && presets != null && i < presets.Length; i++)
        {
            if (Input.GetKeyDown(presetKeys[i]))
                LoadPreset(i);
        }
#endif
    }

    /// <summary>Copy a preset into the test numbers and switch to test numbers. The clock hour is read from the name, e.g. "(Fri 3 AM)".</summary>
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

        Match hour = HourInName.Match(preset.name ?? "");
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

    private int liveCars, liveBuses, liveTrucks, liveGroundBikes, liveFlyoverBikes, liveAllBikes, liveHour;
    private bool liveFlyoverOn, liveHasPrediction;
    private string liveLabel = "waiting for Flask";

    private void OnPredictionUpdated(TrafficAPIManager.TrafficPrediction prediction)
    {
        liveCars = prediction.cars;
        liveBuses = prediction.buses;
        liveTrucks = prediction.trucks;
        liveAllBikes = prediction.motorcycles;
        liveHour = prediction.hour;
        liveHasPrediction = true;
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
        int clockHour;
        if (source == CountSource.LivePrediction)
        {
            predicted[VehicleType.Car] = liveCars;
            predicted[VehicleType.Bike] = liveGroundBikes;   // ground bikes only
            predicted[VehicleType.Bus] = liveBuses;
            predicted[VehicleType.Truck] = liveTrucks;
            predictedFlyoverBikes = liveFlyoverOn ? liveFlyoverBikes : 0;
            flyoverOn = liveFlyoverOn;
            sourceLabel = "Live: " + liveLabel;
            clockHour = liveHour;
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
            clockHour = testHour;
        }

        clockHour = ((clockHour % 24) + 24) % 24;

        if (IsHourlyFlow)
        {
            capNote = "";
            string key = TrafficKey();
            if (key != trafficKey || hourStart != clockHour)
            {
                trafficKey = key;
                hourStart = clockHour;
                StartNewTraffic();
            }
            return;
        }

        trafficKey = null;   // Hourly Flow starts afresh if switched back on
        float ratio = Mathf.Max(1f, vehiclesPerHourPerSpawned);
        var raw = new Dictionary<VehicleType, int>();
        foreach (VehicleType type in Types)
            raw[type] = Mathf.RoundToInt(predicted[type] / ratio);

        Dictionary<VehicleType, int> next = ApplyCap(raw, Mathf.Max(0, maxVehiclesOnRoad));

        bool changed = Types.Any(type => next[type] != target[type]);
        bool rose = Types.Any(type => next[type] > target[type]);
        foreach (VehicleType type in Types)
            target[type] = next[type];

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

    /// <summary>Everything that defines the current traffic; when it changes, a fresh hour starts.</summary>
    private string TrafficKey()
    {
        return $"{source}|{string.Join(",", Types.Select(Predicted))}|{predictedFlyoverBikes}|{flyoverOn}|{RealVehiclesPerVehicle}|{SecondsPerHour}";
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
    // Hourly Flow
    // ==========================================================================================

    /// <summary>
    /// The traffic changed: a fresh hour starts. The road is set to about how busy this traffic makes it
    /// (arrivals per second x seconds to drive the road, for each type): vehicles beyond that are taken
    /// off (or drive off uncounted), missing ones are placed where there is room. The clock then runs a
    /// few settling minutes before the hour, so the road is in step with the new traffic when counting starts.
    /// </summary>
    private void StartNewTraffic()
    {
        CleanUpLists();
        if (clearOldTrafficAtOnce)
        {
            foreach (LaneCarController v in leaving.ToList())   // still driving off from an earlier change
            {
                Forget(v);
                Destroy(v.gameObject);
            }
        }

        hourElapsed = -Mathf.Max(0f, settleMinutes) / 60f * SecondsPerHour;   // e.g. start at 07:55
        LastHour = null;
        ResetHourCounts();

        arrivals.Clear();
        foreach (VehicleType type in Types)
        {
            queued[type] = 0;
            owed[type] = UnityEngine.Random.Range(0f, 1f);   // types do not all arrive at the same moment
            nextArrival[type] = UnityEngine.Random.Range(0.5f, 1.5f);
            target[type] = RoundRandomly(PerSecond(type) * TravelSeconds(type));
        }

        // Keep a random selection of the vehicles already on the road (so the kept ones stay spread
        // along it); the rest vanish, or drive off without being counted.
        foreach (VehicleType type in Types)
        {
            List<LaneCarController> staying = active[type].Where(v => !leaving.Contains(v)).ToList();
            Shuffle(staying);
            int keep = prefillRoad ? target[type] : 0;
            foreach (LaneCarController v in staying.Skip(keep))
            {
                if (clearOldTrafficAtOnce || removeExtrasInstantly)
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

        if (prefillRoad && lanes.Length > 0 && HasCounts)
            FillToTargets();

        if (HasCounts)
        {
            int k = RealVehiclesPerVehicle;
            Debug.Log($"JunctionSpawner: new hour {HourLabel()} ({sourceLabel}). 1 vehicle = {k} real vehicles, 1 hour = {SecondsPerHour:0} s. " +
                      "Entering during the hour: " + string.Join(", ", Types.Select(t => $"{GroupName(t).ToLower()} {Predicted(t) / (float)k:0.#}")) +
                      ". Road filled with " + string.Join(" / ", Types.Select(t => OnRoad(t) - Leaving(t))) +
                      (flyoverOn ? $". Flyover ON: {predictedFlyoverBikes} bikes/hour go up the flyover" : "") + ".");
        }

        CountsChanged?.Invoke();
        HourStarted?.Invoke();
    }

    private void FillToTargets()
    {
        var needed = new List<VehicleType>();
        foreach (VehicleType type in Types)
        {
            int have = active[type].Count(v => !leaving.Contains(v));
            for (int i = have; i < target[type]; i++)
                needed.Add(type);
        }

        Shuffle(needed);
        foreach (VehicleType type in needed)
            SafeSpawn(type, true);
    }

    private void FlowUpdate(float deltaTime)
    {
        CleanUpLists();

        if (removeExtrasInstantly)
        {
            foreach (LaneCarController v in leaving.ToList())
            {
                Forget(v);
                Destroy(v.gameObject);
            }
        }

        if (!ClockRunning)
            return;

        // 1. The clock. At the end of the hour its result is kept and the next hour starts.
        hourElapsed += deltaTime;
        if (hourElapsed >= SecondsPerHour)
            FinishHour();

        // 2. Arrivals at the predicted rate. The gaps vary a little so the traffic does not look
        //    robotic, but over the hour the predicted number arrives (to within one vehicle).
        foreach (VehicleType type in Types)
        {
            if (!CanSpawn(type))
                continue;   // shown as a problem in the readout

            owed[type] += PerSecond(type) * deltaTime;
            while (owed[type] >= nextArrival[type])
            {
                owed[type] -= 1f;
                queued[type]++;
                List<VehicleInfo> choices = templatesByType[type];
                arrivals.Add(new Arrival { type = type, template = choices[UnityEngine.Random.Range(0, choices.Count)] });
                nextArrival[type] = UnityEngine.Random.Range(0.5f, 1.5f);
            }
        }

        // 3. Arrived vehicles enter at the start of a lane, in the order they arrived. One that cannot
        //    get in yet keeps its lanes for itself, so a long truck is not squeezed out by short cars.
        var held = new HashSet<JunctionLane>();
        int index = 0;
        while (index < arrivals.Count && held.Count < lanes.Length)
        {
            Arrival next = arrivals[index];
            if (SafeSpawn(next.type, false, next.template, held) != null)
            {
                arrivals.RemoveAt(index);
                queued[next.type]--;
                if (!IsSettling)
                    enteredThisHour[next.type]++;
                continue;
            }

            foreach (JunctionLane lane in lanes)
                if (lane.Allows(next.type))
                    held.Add(lane);
            index++;
        }
    }

    private bool CanSpawn(VehicleType type) => templatesByType[type].Count > 0 && lanes.Any(lane => lane.Allows(type));

    /// <summary>Rounds up or down at random so that on average the result equals the value (0.3 -> 1 three times in ten).</summary>
    private static int RoundRandomly(float value)
    {
        int whole = Mathf.FloorToInt(value);
        return whole + (UnityEngine.Random.value < value - whole ? 1 : 0);
    }

    private void FinishHour()
    {
        var result = new HourResult
        {
            label = sourceLabel,
            clock = HourLabel(),
            predicted = PredictedTotal,
            counted = CountedTotal,
            countedGround = CountedGround,
            countedFlyover = CountedFlyover,
            groundOnly = ComparingGroundOnly,
            comparedWith = ComparedWith,
            reachedEnd = Types.Select(ReachedEndThisHour).ToArray(),
        };
        int compared = result.groundOnly ? result.countedGround : result.counted;
        result.matchPercent = result.comparedWith > 0 ? 100f * compared / result.comparedWith : 0f;
        LastHour = result;

        Debug.Log($"JunctionSpawner: hour {result.clock} finished ({sourceLabel}). Counted {N(compared)} of {N(result.comparedWith)} predicted" +
                  (result.groundOnly ? " on the ground road" : "") + $" = {result.matchPercent:0.0}%. Reached the end: " +
                  string.Join(", ", Types.Select(t => $"{GroupName(t).ToLower()} {ReachedEndThisHour(t)}")) +
                  (flyoverOn ? $", flyover bikes {flyoverFinished}" : "") + ".");

        hourElapsed = Mathf.Max(0f, hourElapsed - SecondsPerHour);
        ResetHourCounts();
        HourFinished?.Invoke(result);
        HourStarted?.Invoke();
    }

    private void ResetHourCounts()
    {
        foreach (VehicleType type in Types)
        {
            reachedEnd[type] = 0;
            enteredThisHour[type] = 0;
        }
        flyoverFinished = 0;
    }

    private string HourLabel() => $"{hourStart % 24:00}:00-{(hourStart + 1) % 24:00}:00";

    /// <summary>
    /// About how many seconds a vehicle of this type takes from entering to reaching the end. There is
    /// no overtaking, so vehicles often end up behind a slower one: each lane is driven at a speed half
    /// way between the vehicle's own and the slowest vehicle using that lane, plus the average wait at
    /// the red light. Used to fill the road at the start of an hour.
    /// </summary>
    private float TravelSeconds(VehicleType type)
    {
        List<VehicleInfo> own = templatesByType[type];
        List<JunctionLane> allowed = lanes.Where(lane => lane.Allows(type)).ToList();
        if (own.Count == 0 || allowed.Count == 0)
            return 0f;

        float ownSpeed = own.Average(t => t.baseSpeed);
        if (ownSpeed <= 0.01f)
            return 0f;

        float half = own.Average(t => t.HalfLength);
        float endDistance = own.Average(t =>
        {
            LaneCarController driver = t.GetComponent<LaneCarController>();
            return driver != null ? driver.endStopDistance : 2f;
        });

        float total = 0f;
        foreach (JunctionLane lane in allowed)
        {
            float slowest = ownSpeed;
            foreach (VehicleType other in Types)
            {
                if (lane.Allows(other) && Predicted(other) > 0 && templatesByType[other].Count > 0)
                    slowest = Mathf.Min(slowest, templatesByType[other].Average(t => t.baseSpeed));
            }

            float distance = Mathf.Max(0f, lane.Length - half - 0.05f - endDistance);
            total += 0.5f * (distance / ownSpeed + distance / Mathf.Max(0.01f, slowest)) + AverageRedWait(lane.signal);
        }

        return total / allowed.Count;
    }

    private static float AverageRedWait(TrafficSignalController signal)
    {
        if (signal == null)
            return 0f;
        float cycle = signal.redTime + signal.yellowTime + signal.greenTime;
        return cycle > 0f ? signal.redTime * signal.redTime / (2f * cycle) : 0f;
    }

    // ==========================================================================================
    // Keep On Screen: keep the road matching the targets
    // ==========================================================================================

    private void CleanUpLists()
    {
        // Vehicles deleted by something else (e.g. by hand in the Hierarchy) are forgotten.
        foreach (VehicleType type in Types)
            active[type].RemoveAll(v => v == null);
        leaving.RemoveWhere(v => v == null);
        spawnedByMe.RemoveWhere(v => v == null);
    }

    private void Reconcile()
    {
        if (lanes == null || lanes.Length == 0)
            return;

        CleanUpLists();

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

    // ==========================================================================================
    // Placing vehicles
    // ==========================================================================================

    /// <summary>TrySpawn that turns an error into a readout problem instead of stopping the game.</summary>
    private LaneCarController SafeSpawn(VehicleType type, bool anywhere, VehicleInfo template = null, ICollection<JunctionLane> skipLanes = null)
    {
        try
        {
            return TrySpawn(type, anywhere, template, skipLanes);
        }
        catch (Exception exception)
        {
            if (!problems.Any(p => p.StartsWith("Could not place")))
                Debug.LogException(exception, this);
            Problem($"Could not place vehicles: {exception.GetType().Name}: {exception.Message}");
            return null;
        }
    }

    /// <summary>
    /// Places one vehicle of this type (a random template unless one is given): anywhere there is room,
    /// or at the start of a lane. Lanes in skipLanes are not used. Null if there is no room.
    /// </summary>
    private LaneCarController TrySpawn(VehicleType type, bool anywhere, VehicleInfo template = null, ICollection<JunctionLane> skipLanes = null)
    {
        if (template == null)
        {
            if (!templatesByType.TryGetValue(type, out List<VehicleInfo> choices) || choices.Count == 0)
                return null;
            template = choices[UnityEngine.Random.Range(0, choices.Count)];
        }

        float half = template.HalfLength;
        float gap = template.safetyGap;

        foreach (JunctionLane lane in LanesFor(type))
        {
            if (skipLanes != null && skipLanes.Contains(lane))
                continue;

            LaneCarController placed = anywhere
                ? TryPlaceInAnyGap(lane, template, half, gap)
                : TryPlace(lane, template, half + 0.05f, half, gap);   // at the start of the lane
            if (placed != null)
                return placed;
        }

        return null;   // no room right now; it waits and is tried again shortly
    }

    /// <summary>
    /// Works out every stretch of the lane where this vehicle fits (keeping everyone's safety gaps,
    /// staying out of the crossing and away from the ends) and places it at a random spot in one.
    /// </summary>
    private LaneCarController TryPlaceInAnyGap(JunctionLane lane, VehicleInfo template, float half, float gap)
    {
        float low = half;
        float high = lane.Length - lane.endMargin - half;
        if (high <= low)
            return null;

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
            return null;

        // Pick a random spot, bigger gaps being more likely.
        float total = free.Sum(range => range.y - range.x);
        float pick = UnityEngine.Random.Range(0f, total);
        foreach (Vector2 range in free)
        {
            float length = range.y - range.x;
            if (pick <= length)
                return TryPlace(lane, template, range.x + Mathf.Clamp(pick, 0.01f, length - 0.01f), half, gap);
            pick -= length;
        }

        return null;
    }

    private LaneCarController TryPlace(JunctionLane lane, VehicleInfo template, float distance, float half, float gap)
    {
        if (lane.IsNoSpawn(distance, half))
            return null;

        Vector3 position = lane.PointAt(distance);
        if (!lane.Lane.IsClear(position, lane.Direction, half, gap))
            return null;

        VehicleType type = template.type;
        GameObject vehicle = Instantiate(template.gameObject, position, lane.Rotation, GetGroup(type));
        vehicle.name = $"{template.name}_{++spawnCounter:000}";
        vehicle.SetActive(true);   // a copy of a switched-off template would otherwise be switched off too

        var driver = vehicle.GetComponent<LaneCarController>();
        if (driver == null)
        {
            WarnOnce(template.name, $"JunctionSpawner: template {template.name} has no LaneCarController; removed.");
            Destroy(vehicle);
            return null;
        }

        driver.Setup(lane.Lane, lane.stopLine, lane.signal, lane.endPoint, template.PickSpeed());
        driver.ReachedEnd += OnVehicleReachedEnd;
        active[type].Add(driver);
        spawnedByMe.Add(driver);

        if (spawnCounter == 1)
            LogFirstVehicle(vehicle, lane);
        return driver;
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
        // Hourly Flow: every vehicle of the current traffic that reaches the end is counted.
        if (IsHourlyFlow && ClockRunning && !IsSettling && !leaving.Contains(vehicle) && vehicle.Info != null)
            reachedEnd[vehicle.Info.type]++;

        Forget(vehicle);   // Keep On Screen: the next Reconcile places a replacement if this type is short
    }

    private void Forget(LaneCarController vehicle)
    {
        vehicle.ReachedEnd -= OnVehicleReachedEnd;
        leaving.Remove(vehicle);
        spawnedByMe.Remove(vehicle);
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

    /// <summary>A whole number with thousands separators, e.g. 20,792.</summary>
    private static string N(int value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private void WarnOnce(string key, string message)
    {
        if (warnedOnce.Add(key))
            Debug.LogWarning(message);
    }

    // ==========================================================================================
    // Readout (top-left box; Adithya's UI panel can show the same numbers from the public members)
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

        GUILayout.BeginArea(new Rect(10f, 10f, 500f, Screen.height - 20f));
        GUILayout.BeginVertical(GUI.skin.box);

        if (IsHourlyFlow)
        {
            GUILayout.Label("<b>Junction simulation</b>   " + sourceLabel + "\n" + ScaleLine(), readoutStyle);
            GUILayout.Label(ClockLine(), readoutStyle);
            DrawProgressBar(HourProgress);

            Row("<b>Type</b>", "<b>Per hour</b>", "<b>Reached end</b>", "<b>Counted</b>", "<b>On road</b>", "<b>Waiting</b>");
            foreach (VehicleType type in Types)
                Row(type == VehicleType.Bike ? "Bikes*" : GroupName(type), N(Predicted(type)), ReachedEndThisHour(type).ToString(),
                    N(Counted(type)), OnRoad(type).ToString(), Waiting(type).ToString());
            Row("<b>Total</b>", N(PredictedGround), Types.Sum(ReachedEndThisHour).ToString(), N(CountedGround),
                Types.Sum(OnRoad).ToString(), Types.Sum(Waiting).ToString());

            GUILayout.Label(FlowFooter(), readoutStyle);
        }
        else
        {
            GUILayout.Label("<b>Junction spawner</b>   " + sourceLabel +
                            $"\n1 vehicle on screen = {vehiclesPerHourPerSpawned:0} per hour", readoutStyle);

            Row("<b>Type</b>", "<b>Predicted</b>", "<b>Target</b>", "<b>On road</b>", "<b>Waiting*</b>", "<b>Leaving</b>");
            foreach (VehicleType type in Types)
                Row(type == VehicleType.Bike ? "Bikes*" : GroupName(type), Predicted(type).ToString(), Target(type).ToString(),
                    OnRoad(type).ToString(), Waiting(type).ToString(), Leaving(type).ToString());
            Row("<b>Total</b>", Types.Sum(Predicted).ToString(), Types.Sum(Target).ToString(), Types.Sum(OnRoad).ToString(),
                Types.Sum(Waiting).ToString(), Types.Sum(Leaving).ToString());

            GUILayout.Label(KeepOnScreenFooter(), readoutStyle);
        }

        GUILayout.EndVertical();
        GUILayout.EndArea();
    }

    private void Row(params string[] cells)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label(cells[0], readoutStyle, GUILayout.Width(70f));
        for (int i = 1; i < cells.Length; i++)
            GUILayout.Label(cells[i], readoutStyle, GUILayout.Width(i == 2 ? 90f : 70f));
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
        if (IsHourlyFlow)
        {
            text.AppendLine("<b>Junction simulation</b>   " + sourceLabel);
            text.AppendLine(ScaleLine());
            int filled = Mathf.RoundToInt(HourProgress * 20f);
            text.AppendLine(ClockLine() + "   [" + new string('#', filled) + new string('-', 20 - filled) + "]");
            text.AppendLine();
            text.AppendLine("<b>Type        Per hour   Reached end    Counted   On road   Waiting</b>");
            foreach (VehicleType type in Types)
            {
                string label = type == VehicleType.Bike ? "Bikes*" : GroupName(type);
                text.AppendLine($"{label,-10}  {N(Predicted(type)),8}   {ReachedEndThisHour(type),11}   {N(Counted(type)),8}   {OnRoad(type),7}   {Waiting(type),7}");
            }
            text.AppendLine($"{"Total",-10}  {N(PredictedGround),8}   {Types.Sum(ReachedEndThisHour),11}   {N(CountedGround),8}   {Types.Sum(OnRoad),7}   {Types.Sum(Waiting),7}");
            text.Append(FlowFooter());
            return text.ToString();
        }

        text.AppendLine("<b>Junction spawner</b>   " + sourceLabel);
        text.AppendLine($"1 vehicle on screen = {vehiclesPerHourPerSpawned:0} per hour");
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
        text.Append(KeepOnScreenFooter());
        return text.ToString();
    }

    private string ScaleLine()
    {
        int seconds = Mathf.RoundToInt(SecondsPerHour);
        return $"1 vehicle = {N(RealVehiclesPerVehicle)} real vehicles   |   1 hour = {seconds / 60}:{seconds % 60:00} min";
    }

    private string ClockLine()
    {
        if (!HasCounts)
            return "<b>Clock --:--</b>   waiting for the first prediction";
        if (IsSettling)
            return $"<b>Clock {ClockText}</b>   road settling with this traffic, counting starts at {hourStart % 24:00}:00";
        return $"<b>Clock {ClockText}</b>   (hour {HourProgress * 100f:0}% done)";
    }

    private static string MatchColour(float percent)
    {
        return percent >= 95f && percent <= 105f ? "#6bff8f" : percent >= 85f && percent <= 115f ? "#ffcc66" : "#ff6b6b";
    }

    private string FlowFooter()
    {
        var text = new StringBuilder();
        foreach (string problem in problems)
            text.AppendLine("<color=#ff6b6b><b>Problem:</b> " + problem + "</color>");
        foreach (VehicleType type in Types)
        {
            if (Predicted(type) > 0 && templatesByType.TryGetValue(type, out var list) && list.Count == 0)
                text.AppendLine($"<color=#ff6b6b><b>Problem:</b> no {GroupName(type)} templates. Drag all prefabs from " +
                                "Assets/JunctionSim/Prefabs into Templates.</color>");
        }

        // Counted so far and where it is heading
        text.AppendLine($"<b>Counted for the hour:</b>  {N(CountedTotal)} / {N(PredictedTotal)}");
        int projected = ProjectedHourTotal;
        if (projected >= 0)
        {
            float match = MatchPercent;
            text.AppendLine($"<b>At this rate, 1 hour =</b> {N(projected)}   " +
                            $"<color={MatchColour(match)}>({match:0.0}% of the {(ComparingGroundOnly ? "ground prediction" : "dashboard")} {N(ComparedWith)})</color>");
        }
        else if (HasCounts)
        {
            text.AppendLine("<b>At this rate, 1 hour =</b> ... (shown after the first 6 minutes)");
        }

        // A quiet hour has so few vehicles that one more or less moves the match a lot.
        float vehiclesThisHour = ComparedWith / (float)RealVehiclesPerVehicle;
        if (HasCounts && vehiclesThisHour > 0f && vehiclesThisHour < 60f)
            text.AppendLine($"<i>Quiet hour: only about {vehiclesThisHour:0} vehicles pass in the whole hour, so one more or less " +
                            $"changes the match by {100f / vehiclesThisHour:0}%.</i>");

        // Flyover
        if (flyoverOn && predictedFlyoverBikes > 0)
        {
            text.AppendLine($"Before flyover {N(PredictedTotal)}  ->  after {N(PredictedGround)}   (bikes to the flyover {N(predictedFlyoverBikes)})");
            text.AppendLine($"Ground counted {N(CountedGround)} / {N(PredictedGround)}   |   Flyover counted {N(CountedFlyover)} / {N(predictedFlyoverBikes)}");
            if (!flyoverBikesReported)
                text.AppendLine("<color=#ffcc66>No flyover bike counted yet (flyover bike simulation), so the match uses the ground road only.</color>");
        }

        if (LastHour != null)
        {
            int compared = LastHour.groundOnly ? LastHour.countedGround : LastHour.counted;
            text.AppendLine($"Last full hour ({LastHour.clock}): counted {N(compared)} of {N(LastHour.comparedWith)} = " +
                            $"<color={MatchColour(LastHour.matchPercent)}>{LastHour.matchPercent:0.0}%</color>");
        }

        // Congestion and left-overs
        int waiting = Types.Sum(Waiting);
        if (waiting >= 5)
            text.AppendLine($"<color=#ffcc66>{waiting} vehicles waiting to enter: they arrive faster than the road takes them in (congestion), " +
                            "so the count falls behind the prediction.</color>");
        int leftOver = Types.Sum(Leaving);
        if (leftOver > 0)
            text.AppendLine($"{leftOver} vehicles from the previous traffic are driving off (not counted).");

        text.AppendLine("Bikes* = ground bikes only" + (source == CountSource.LivePrediction
            ? $"; all bikes {N(PredictedAllBikes)}/hour, flyover {(flyoverOn ? "ON" : "OFF")}"
            : ""));
        if (!string.IsNullOrEmpty(lastError) && source == CountSource.LivePrediction)
            text.AppendLine("<color=#ff6b6b>Flask error, keeping current traffic: </color>" + lastError);
        text.Append("<i>Keys: 1-5 test presets | G hide this box</i>");
        return text.ToString();
    }

    private string KeepOnScreenFooter()
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
        text.Append("<i>Keys: 1-5 test presets | G hide this box</i>");
        return text.ToString();
    }
}