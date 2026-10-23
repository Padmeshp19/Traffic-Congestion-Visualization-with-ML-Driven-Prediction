using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
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
    private int[] appliedTestNumbers = new int[4];
    private readonly HashSet<string> warnedOnce = new HashSet<string>();
    private GUIStyle readoutStyle;

    /// <summary>Sent whenever the targets change (useful for the UI panel).</summary>
    public event Action CountsChanged;

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
        if (lanes == null || lanes.Length == 0)
            lanes = SceneManager.GetActiveScene().GetRootGameObjects()
                .SelectMany(root => root.GetComponentsInChildren<JunctionLane>(true))
                .ToArray();

        lanes = lanes.Where(lane => lane != null && lane.IsValid).ToArray();
        if (lanes.Length == 0)
            Debug.LogError("JunctionSpawner: no usable JunctionLane found. Run Tools > Junction Sim > Set Up Lanes first.");

        foreach (VehicleType type in Types)
            templatesByType[type] = (templates ?? new VehicleInfo[0]).Where(t => t != null && t.type == type).ToList();

        foreach (VehicleType type in Types)
        {
            if (templatesByType[type].Count == 0)
                Debug.LogWarning($"JunctionSpawner: no {type} template assigned, so no {type}s can be spawned.");
            if (!lanes.Any(lane => lane.Allows(type)))
                Debug.LogWarning($"JunctionSpawner: no lane allows {type}s.");
        }

        // Spawned vehicles go under their own top-level object, so the spawner's own position,
        // rotation or scale (or whatever object it sits inside) can never move, turn or squash them.
        var container = new GameObject("JunctionSim_Vehicles").transform;
        foreach (VehicleType type in Types)
        {
            var group = new GameObject(GroupName(type)).transform;
            group.SetParent(container, false);
            groups[type] = group;
        }

        if (switchOffOldVehicles)
            SwitchOffOldVehicles();

        appliedSource = source;
        countsDirty = true;
        fillAnywhere = true;
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
        Debug.Log($"JunctionSpawner: test preset {index + 1}, {preset.name}.");
    }

    private bool TestNumbersChanged()
    {
        int[] now = { testCars, testBikes, testBuses, testTrucks };
        if (now.SequenceEqual(appliedTestNumbers))
            return false;
        appliedTestNumbers = now;
        return true;
    }

    // ==========================================================================================
    // Live prediction (TrafficAPIManager)
    // ==========================================================================================

    private int liveCars, liveBuses, liveTrucks, liveGroundBikes, liveFlyoverBikes, liveAllBikes;
    private bool liveFlyoverOn;
    private string liveLabel = "waiting for Flask";

    private void OnPredictionUpdated(TrafficAPIManager.TrafficPrediction prediction)
    {
        liveCars = prediction.cars;
        liveBuses = prediction.buses;
        liveTrucks = prediction.trucks;
        liveAllBikes = prediction.motorcycles;
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
        if (source == CountSource.LivePrediction)
        {
            predicted[VehicleType.Car] = liveCars;
            predicted[VehicleType.Bike] = liveGroundBikes;   // ground bikes only
            predicted[VehicleType.Bus] = liveBuses;
            predicted[VehicleType.Truck] = liveTrucks;
            predictedFlyoverBikes = liveFlyoverBikes;
            flyoverOn = liveFlyoverOn;
            sourceLabel = "Live: " + liveLabel;
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
        }

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
        if (lanes.Length == 0)
            return;

        // Vehicles deleted by something else (e.g. by hand in the Hierarchy) are forgotten.
        foreach (VehicleType type in Types)
            active[type].RemoveAll(v => v == null);
        leaving.RemoveWhere(v => v == null);
        spawnedByMe.RemoveWhere(v => v == null);

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
            TrySpawn(type, anywhere);

        fillAnywhere = false;
    }

    private bool TrySpawn(VehicleType type, bool anywhere)
    {
        List<VehicleInfo> choices = templatesByType[type];
        if (choices.Count == 0)
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
            else if (TryPlace(lane, template, half + 0.05f, half, gap))
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
                return TryPlace(lane, template, range.x + Mathf.Clamp(pick, 0.01f, length - 0.01f), half, gap);
            pick -= length;
        }

        return false;
    }

    private bool TryPlace(JunctionLane lane, VehicleInfo template, float distance, float half, float gap)
    {
        if (lane.IsNoSpawn(distance, half))
            return false;

        Vector3 position = lane.PointAt(distance);
        if (!lane.Lane.IsClear(position, lane.Direction, half, gap))
            return false;

        VehicleType type = template.type;
        GameObject vehicle = Instantiate(template.gameObject, position, lane.Rotation, groups[type]);
        vehicle.name = $"{template.name}_{++spawnCounter:000}";

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
        Forget(vehicle);   // the next Reconcile places a replacement if this type is short
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

        GUILayout.BeginArea(new Rect(10f, 10f, 440f, Screen.height - 20f));
        GUILayout.BeginVertical(GUI.skin.box);
        GUILayout.Label("<b>Junction spawner</b>   " + sourceLabel +
                        $"\n1 vehicle on screen = {vehiclesPerHourPerSpawned:0} per hour", readoutStyle);

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

    public string BuildReadout()
    {
        var text = new StringBuilder();
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
        text.Append(ReadoutFooter());
        return text.ToString();
    }

    private string ReadoutFooter()
    {
        var text = new StringBuilder();
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