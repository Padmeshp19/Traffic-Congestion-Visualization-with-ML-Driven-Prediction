"""Dynamic flyover diversion model.

The model is deliberately independent of Flask so it can be tested and reused
by the API, dashboard endpoints, and sensitivity analysis.
"""
import json
import math
from pathlib import Path


DEFAULTS = {
    "t0": 3.0,
    "alpha": 2.49,
    "beta": 4.0,
    "calib_vc": 0.85,
    "peak_ratio": 2.3,
    "eligible_share": 0.40,
    "flyover_time_factor": 0.85,
    "ramp_penalty": 1.5,
    "logit_k": 2.0,
    "flyover_ratio": 0.30,
    "bike_pcu": 0.5,
    "car_pcu": 1.0,
    "bus_pcu": 3.0,
    "truck_pcu": 3.0,
    "max_iterations": 200,
    "tolerance": 1e-3,
}


def load_params(path=None):
    path = Path(path or Path(__file__).parent / "data" / "flyover_params.json")
    if not path.exists():
        return {"_default": dict(DEFAULTS), "_meta": {"fallback": 0.30}}
    with path.open(encoding="utf-8") as handle:
        return json.load(handle)


def _params_for_road(params, road):
    selected = dict(DEFAULTS)
    selected.update(params.get("_default", {}))
    selected.update(params.get(road, {}))
    return selected


def _pcu(row, p):
    return (
        row["cars"] * p["car_pcu"]
        + row["motorcycles"] * p["bike_pcu"]
        + row["buses"] * p["bus_pcu"]
        + row["trucks"] * p["truck_pcu"]
    )


def _time_from_vc(vc, p):
    return p["t0"] * (1.0 + p["alpha"] * max(0.0, vc) ** p["beta"])


def _probability(minutes_saved, p):
    value = max(-60.0, min(60.0, p["logit_k"] * minutes_saved))
    return 1.0 / (1.0 + math.exp(-value))


def _normalise_row(row):
    return {
        "cars": max(0, float(row.get("cars", row.get("Cars", 0)))),
        "motorcycles": max(0, float(row.get("motorcycles", row.get("bikes", row.get("Motorcycles", 0))))),
        "buses": max(0, float(row.get("buses", row.get("Buses", 0)))),
        "trucks": max(0, float(row.get("trucks", row.get("Trucks", 0)))),
    }


def compute_day(hourly_counts, road=None, params=None, overrides=None):
    """Compute diversion and benefit metrics for 24 hourly predictions.

    ``hourly_counts`` is an iterable of mappings with cars, motorcycles, buses,
    and trucks. Overrides are intended for sensitivity analysis.
    """
    params = params or {}
    p = _params_for_road(params, road) if road else {**DEFAULTS, **params}
    if overrides:
        p.update({k: v for k, v in overrides.items() if v is not None})

    rows = [_normalise_row(row) for row in hourly_counts]
    if not rows:
        return {"hours": [], "peak_vc": 0.0, "vehicle_hours_saved": 0.0,
                "useful_hours": 0, "peak_diversion_rate": 0.0}

    fallback = params.get("_meta", {}).get("fallback") if road else None
    if fallback is not None and road not in params:
        hours = []
        for hour, row in enumerate(rows):
            diverted = min(row["motorcycles"], row["motorcycles"] * float(fallback))
            hours.append({
                "hour": hour,
                "diverted": int(round(diverted)),
                "diversion_rate": diverted / row["motorcycles"] if row["motorcycles"] else 0.0,
                "vc_before": 0.0,
                "vc_after": 0.0,
                "ground_time_min": p["t0"],
                "ground_time_after_min": p["t0"],
                "vehicle_hours_saved": 0.0,
            })
        return {
            "hours": hours,
            "peak_vc": 0.0,
            "vehicle_hours_saved": 0.0,
            "useful_hours": sum(item["diversion_rate"] >= 0.10 for item in hours),
            "peak_diversion_rate": max(item["diversion_rate"] for item in hours),
            "capacity_pcu_per_hour": 0.0,
            "fallback": True,
        }

    pcu_values = [_pcu(row, p) for row in rows]
    capacity = max(pcu_values) / max(float(p["peak_vc"]), 0.01)
    flyover_time = float(p["t0"]) * float(p["flyover_time_factor"])
    results = []

    for hour, (row, pcu) in enumerate(zip(rows, pcu_values)):
        bikes = row["motorcycles"]
        before_vc = pcu / capacity if capacity else 0.0
        diverted = 0.0
        cap = float(p["flyover_ratio"]) * capacity / max(float(p["bike_pcu"]), 0.01)
        for _ in range(int(p["max_iterations"])):
            after_pcu = max(0.0, pcu - diverted * float(p["bike_pcu"]))
            ground_vc = after_pcu / capacity if capacity else 0.0
            ground_time = _time_from_vc(ground_vc, p)
            target = min(
                float(p["eligible_share"]) * bikes *
                _probability(ground_time - flyover_time - float(p["ramp_penalty"]), p),
                cap,
            )
            updated = 0.5 * diverted + 0.5 * target
            if abs(updated - diverted) <= float(p["tolerance"]):
                diverted = updated
                break
            diverted = updated

        after_pcu = max(0.0, pcu - diverted * float(p["bike_pcu"]))
        after_vc = after_pcu / capacity if capacity else 0.0
        before_time = _time_from_vc(before_vc, p)
        after_time = _time_from_vc(after_vc, p)
        before_minutes = (row["cars"] + bikes + row["buses"] + row["trucks"]) * before_time
        after_minutes = (
            (row["cars"] + bikes - diverted + row["buses"] + row["trucks"]) * after_time
            + diverted * (flyover_time + float(p["ramp_penalty"]))
        )
        saved_hours = max(before_minutes - after_minutes, 0.0) / 60.0
        results.append({
            "hour": hour,
            "diverted": int(round(diverted)),
            "diversion_rate": diverted / bikes if bikes else 0.0,
            "vc_before": before_vc,
            "vc_after": after_vc,
            "ground_time_min": before_time,
            "ground_time_after_min": after_time,
            "vehicle_hours_saved": saved_hours,
        })

    return {
        "hours": results,
        "peak_vc": max(item["vc_before"] for item in results),
        "vehicle_hours_saved": sum(item["vehicle_hours_saved"] for item in results),
        "useful_hours": sum(item["diversion_rate"] >= 0.10 for item in results),
        "peak_diversion_rate": max(item["diversion_rate"] for item in results),
        "capacity_pcu_per_hour": capacity,
    }


def recommendation(summary):
    """Return a benefit verdict plus the separate site-readiness layer."""
    peak_vc = summary["peak_vc"]
    saved = summary["vehicle_hours_saved"]
    useful = summary["useful_hours"]
    if peak_vc >= 0.85:
        readiness = "Near-gridlock"
    elif peak_vc >= 0.65:
        readiness = "Heavy"
    else:
        readiness = "Moderate"

    if saved >= 1.0 and useful >= 2 and peak_vc >= 0.85:
        verdict = "Prioritize"
    elif saved > 0.0 and useful >= 1 and peak_vc >= 0.65:
        verdict = "Consider"
    else:
        verdict = "Monitor"
    return {"verdict": verdict, "site_readiness": readiness}
