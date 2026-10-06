# Traffic Congestion Visualization with ML-Driven Prediction

Capstone project for visualizing and predicting traffic congestion using a Flask/ML backend and a Unity junction simulation.

## Project components

```text
app.py                              Flask API and dashboard server
train_model.py                     Trains the traffic prediction models
requirements.txt                   Python dependencies
models/                            Generated model metadata and trained model files
templates/dashboard.html           Browser dashboard
Unity-Sem7/                        Unity traffic simulation
Unity-Sem7/Assets/TrafficAPIManager.cs
                                    Unity-Flask integration added by Aryan Urs
```

## Backend setup

From the repository root, create and activate a virtual environment:

### Windows PowerShell

```powershell
python -m venv venv
.\venv\Scripts\Activate.ps1
```

### macOS/Linux

```bash
python3 -m venv venv
source venv/bin/activate
```

Install the required Python packages:

```bash
python -m pip install --upgrade pip
pip install -r requirements.txt
```

If the trained model files are missing from `models/`, generate them before starting Flask:

```bash
python train_model.py
```

Start the Flask server:

```bash
python app.py
```

The API is available at:

```text
http://127.0.0.1:5000
```

## Test the prediction API

The Unity integration uses `POST /predict` with this JSON request format:

```json
{
  "road": "Silk Board Junction",
  "day_num": 4,
  "hour": 8,
  "flyover": false,
  "model": "gbm"
}
```

Example using PowerShell:

```powershell
$body = @{
    road = "Silk Board Junction"
    day_num = 4
    hour = 8
    flyover = $false
    model = "gbm"
} | ConvertTo-Json

Invoke-RestMethod `
    -Uri http://127.0.0.1:5000/predict `
    -Method Post `
    -ContentType "application/json" `
    -Body $body
```

The response includes:

```text
model, road, day_num, hour, peak_flag, flyover,
cars, motorcycles, buses, trucks, total
```

The day-number mapping is:

```text
0 = Monday
1 = Tuesday
2 = Wednesday
3 = Thursday
4 = Friday
5 = Saturday
6 = Sunday
```

## Aryan's Unity-Flask integration

`Unity-Sem7/Assets/TrafficAPIManager.cs` connects the Unity simulation to the Flask prediction API.

### Responsibilities

`TrafficAPIManager`:

- sends live prediction requests to Flask using `POST /predict`;
- parses the vehicle-count JSON response;
- exposes the latest prediction to Unity scripts;
- periodically refreshes the prediction;
- provides Flyover ON/OFF behavior;
- routes 30% of predicted motorcycles to the flyover when enabled;
- preserves the total motorcycle count;
- reports API and JSON parsing errors in the Unity Console.

### Default Inspector settings

After adding `TrafficAPIManager.cs` to a Unity GameObject, the default settings are:

```text
Flask Base URL:                  http://127.0.0.1:5000
Road:                            Silk Board Junction
Day Number:                      4 (Friday)
Hour:                            8 (8:00 AM)
Model:                           gbm
Refresh Interval:                30 seconds
Flyover Bike Routing Percentage: 0.30
```

Use `http://127.0.0.1:5000` when Flask and Unity run on the same computer. Start Flask before entering Play mode in Unity.

### Unity events

The manager exposes these events for the spawner and flyover scripts:

```csharp
PredictionUpdated
MotorcycleRoutingUpdated
RequestFailed
```

`PredictionUpdated` provides a `TrafficPrediction` object with:

```text
cars
motorcycles
buses
trucks
total
road
day_num
hour
peak_flag
```

`MotorcycleRoutingUpdated` provides:

```text
groundMotorcycles
flyoverMotorcycles
```

A spawner can subscribe as follows:

```csharp
private TrafficAPIManager trafficAPI;

private void OnEnable()
{
    trafficAPI = FindFirstObjectByType<TrafficAPIManager>();

    if (trafficAPI != null)
    {
        trafficAPI.PredictionUpdated += OnPredictionUpdated;
        trafficAPI.MotorcycleRoutingUpdated += OnMotorcycleRoutingUpdated;
        trafficAPI.RequestFailed += OnRequestFailed;
    }
}

private void OnPredictionUpdated(
    TrafficAPIManager.TrafficPrediction prediction)
{
    // Spawn/update cars, buses, and trucks using prediction counts.
}

private void OnMotorcycleRoutingUpdated(
    int groundMotorcycles,
    int flyoverMotorcycles)
{
    // Spawn groundMotorcycles on the junction lanes.
    // Pass flyoverMotorcycles to the flyover simulation.
}

private void OnRequestFailed(string error)
{
    Debug.LogError(error);
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
```

### Flyover behavior

Unity sends `flyover: false` to Flask intentionally. The Flask endpoint has its own flyover-reduction option, but the Unity simulation performs the route split locally to avoid applying the 30% reduction twice.

For a prediction of 100 motorcycles:

```text
Flyover OFF:
  Ground motorcycles = 100
  Flyover motorcycles = 0

Flyover ON:
  Ground motorcycles = 70
  Flyover motorcycles = 30
```

The conservation rule is:

```text
Ground motorcycles + Flyover motorcycles
= predicted motorcycles
```

Press `F` during Unity Play mode to toggle Flyover ON/OFF when keyboard toggling is enabled in the Inspector.

## Unity setup

The Unity project uses the version recorded in:

```text
Unity-Sem7/ProjectSettings/ProjectVersion.txt
```

Current project version:

```text
Unity 6000.3.7f1
```

1. Open `Unity-Sem7/` in Unity Hub.
2. Open `Assets/Traffic junction.unity`.
3. Create or select a GameObject for `TrafficAPIManager`.
4. Attach `TrafficAPIManager.cs`.
5. Confirm the Flask URL and prediction settings.
6. Start Flask with `python app.py`.
7. Press Play in Unity.
8. Check the Unity Console for a successful prediction message.

Existing movement scripts include:

```text
LaneCarController.cs       Vehicle movement, signals, and lane spacing
LaneManager.cs             Vehicle registration and anti-collision checks
TrafficSignalController.cs Traffic-light state changes
```

Every spawned vehicle using `LaneCarController` must have a valid `LaneManager` reference. Otherwise Unity logs:

```text
<vehicle name> has NO LaneManager assigned!
```

## Team integration responsibilities

```text
Aryan Urs   Flask connection, TrafficAPIManager, 30% bike route split
Swathi M    Junction vehicle spawner and ground traffic simulation
Adithya C   UI panel, flyover motorcycle presentation and movement
Padmesh     ML models, Flask backend and dashboard model selector
```

The expected runtime flow is:

```text
Flask /predict
    ↓
TrafficAPIManager.cs
    ↓
PredictionUpdated
    ├── Swathi's junction spawner receives cars, buses, trucks
    └── Swathi receives ground motorcycle count

MotorcycleRoutingUpdated
    ├── Swathi uses groundMotorcycles
    └── Adithya uses flyoverMotorcycles
```

Do not spawn `flyoverMotorcycles` again on the ground. This would duplicate motorcycles in the simulation.

## Integration checklist

```text
[ ] Flask starts successfully
[ ] POST /predict returns the required fields
[ ] Unity uses the correct Flask URL
[ ] TrafficAPIManager is attached to a scene GameObject
[ ] Unity Console shows a received prediction
[ ] Spawner subscribes to PredictionUpdated
[ ] Spawner subscribes to MotorcycleRoutingUpdated
[ ] Flyover OFF sends all motorcycles to the ground route
[ ] Flyover ON routes floor(predicted motorcycles × 0.30) to the flyover
[ ] Ground + flyover motorcycles equals the predicted count
[ ] Ground and flyover vehicles use separate spawn points/lanes
[ ] Vehicles have valid LaneManager references
[ ] Low-traffic and peak-traffic scenarios are tested
[ ] No vehicles spawn outside the road, overlap, float, or clip through the mesh
```
