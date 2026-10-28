using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// Makes Unity follow the junction / day / hour / model / flyover chosen on the Flask dashboard.
/// Add this to the same GameObject as TrafficAPIManager (or drag it into the Api field).
/// It only calls TrafficAPIManager's existing public methods, so no other script is changed.
/// Untick "Follow Dashboard" to go back to the values typed in the Inspector.
/// </summary>
public class DashboardSelectionSync : MonoBehaviour
{
    [Serializable]
    private class Selection
    {
        public string road;
        public int day_num;
        public int hour;
        public string model;
        public bool flyover;
        public int seq;
    }

    [SerializeField] private TrafficAPIManager api;
    [SerializeField] private bool followDashboard = true;
    [SerializeField] private bool followFlyover = true;
    [SerializeField] private float pollSeconds = 1f;

    private int lastSeq = -1;

    private void Reset() { api = GetComponent<TrafficAPIManager>(); }

    private void OnEnable()
    {
        if (api == null) api = GetComponent<TrafficAPIManager>();
        StartCoroutine(PollLoop());
    }

    private IEnumerator PollLoop()
    {
        while (true)
        {
            if (followDashboard && api != null)
                yield return Poll();
            yield return new WaitForSeconds(Mathf.Max(0.25f, pollSeconds));
        }
    }

    private IEnumerator Poll()
    {
        using (UnityWebRequest req = UnityWebRequest.Get(api.FlaskBaseUrl.TrimEnd('/') + "/dashboard-selection"))
        {
            req.timeout = 5;
            yield return req.SendWebRequest();
            if (req.result != UnityWebRequest.Result.Success) yield break;

            Selection s = JsonUtility.FromJson<Selection>(req.downloadHandler.text);
            // seq stays 0 until the dashboard has sent something, so Unity keeps its Inspector values.
            if (s == null || s.seq == 0 || s.seq == lastSeq || string.IsNullOrEmpty(s.road)) yield break;
            lastSeq = s.seq;

            if (!string.Equals(api.Model, s.model, StringComparison.OrdinalIgnoreCase))
                api.SetModel(s.model);
            api.SetQuery(s.road, s.day_num, s.hour);
            if (followFlyover && api.FlyoverOn != s.flyover)
                api.SetFlyover(s.flyover);

            Debug.Log($"DashboardSelectionSync: following dashboard -> {s.road}, day {s.day_num}, {s.hour:00}:00, {s.model}, flyover {(s.flyover ? "ON" : "OFF")}");
        }
    }
}
