
using UnityEngine;

public class TrafficHUD : MonoBehaviour
{
    public TrafficAPIManager trafficAPI;

    private GUIStyle titleStyle;
    private GUIStyle labelStyle;

    private void Awake()
    {
        if (trafficAPI == null)
        {
            trafficAPI = FindFirstObjectByType<TrafficAPIManager>();
        }
    }

    private void OnGUI()
    {
        if (titleStyle == null)
        {
            titleStyle = new GUIStyle(GUI.skin.label);
            titleStyle.fontSize = 20;
            titleStyle.fontStyle = FontStyle.Bold;
            titleStyle.normal.textColor = Color.white;

            labelStyle = new GUIStyle(GUI.skin.label);
            labelStyle.fontSize = 15;
            labelStyle.normal.textColor = Color.white;
        }

        GUILayout.BeginArea(
            new Rect(15, 15, 340, 420),
            GUI.skin.box
        );

        GUILayout.BeginVertical();

        GUILayout.Label("TRAFFIC PREDICTION", titleStyle);
        GUILayout.Space(8);

        if (trafficAPI == null)
        {
            GUILayout.Label("Traffic API Manager not found.", labelStyle);
            GUILayout.EndVertical();
            GUILayout.EndArea();
            return;
        }

        GUILayout.Label("Junction: " + trafficAPI.Road, labelStyle);
        GUILayout.Label(
            "Day: " + TrafficAPIManager.DayName(trafficAPI.DayNumber)
            + " | Hour: " + trafficAPI.Hour.ToString("00") + ":00",
            labelStyle
        );
        GUILayout.Label("Model: " + trafficAPI.Model.ToUpperInvariant(), labelStyle);

        GUILayout.Space(10);

        var prediction = trafficAPI.CurrentPrediction;

        if (prediction != null)
        {
            GUILayout.Label("Cars: " + prediction.cars, labelStyle);
            GUILayout.Label("Motorcycles: " + prediction.motorcycles, labelStyle);
            GUILayout.Label("Buses: " + prediction.buses, labelStyle);
            GUILayout.Label("Trucks: " + prediction.trucks, labelStyle);

            GUILayout.Space(6);
            GUILayout.Label("Total vehicles: " + prediction.total, titleStyle);

            GUILayout.Space(8);
            GUILayout.Label(
                "Ground motorcycles: " + trafficAPI.GroundMotorcycles,
                labelStyle
            );
            GUILayout.Label(
                "Flyover motorcycles: " + trafficAPI.FlyoverMotorcycles,
                labelStyle
            );
        }
        else
        {
            GUILayout.Label(
                trafficAPI.IsRequestRunning
                    ? "Loading prediction..."
                    : "Waiting for prediction...",
                labelStyle
            );
        }

        GUILayout.Space(8);

        GUILayout.Label(
            trafficAPI.FlyoverOn ? "Flyover: ON" : "Flyover: OFF",
            titleStyle
        );

        if (GUILayout.Button(
            trafficAPI.FlyoverOn ? "Disable Flyover" : "Enable Flyover",
            GUILayout.Height(32)))
        {
            trafficAPI.SetFlyover(!trafficAPI.FlyoverOn);
        }

        if (GUILayout.Button("Refresh Prediction", GUILayout.Height(28)))
        {
            trafficAPI.RefreshNow();
        }

        if (!string.IsNullOrEmpty(trafficAPI.LastError))
        {
            GUILayout.Label("API Error: " + trafficAPI.LastError, labelStyle);
        }
        else if (trafficAPI.HasPrediction)
        {
            GUILayout.Label("API: Connected - prediction received", labelStyle);
        }
        else
        {
            GUILayout.Label("API: Waiting for first prediction", labelStyle);
        }

        GUILayout.EndVertical();
        GUILayout.EndArea();
    }
}
