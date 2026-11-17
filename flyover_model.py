"""Dynamic flyover diversion model (item 1).

Per junction and hour:
  ground time  t_g = t0 * (1 + alpha * (v/c)^beta)      (BPR form, alpha CALIBRATED)
  bikes divert with probability p = logistic(k * (t_g - t_flyover - ramp_penalty))
  iterate, because bikes leaving lowers v/c, which lowers t_g, which lowers p.

alpha is calibrated ONCE so that at v/c = calib_vc the travel time is
`peak_ratio` x free-flow (from Google Maps typical traffic). Every road then
uses that same curve, and roads differ only through their own peak_vc.
Capacity is set so the busiest hour of the day sits at `peak_vc`
(counts are area-level, so absolute capacity is meaningless; v/c is relative).
"""
import json
import math
import os

_HERE = os.path.dirname(os.path.abspath(__file__))
PARAMS_PATH = os.path.join(_HERE, "data", "flyover_params.json")

_cache = None


def _load(path=PARAMS_PATH):
    global _cache
    if _cache is None or _cache[0] != path:
        try:
            with open(path, "r", encoding="utf-8") as f:
                _cache = (path, json.load(f))
        except (OSError, ValueError):
            _cache = (path, {})
    return _cache[1]


def fallback_diversion():
    return float(_load().get("_meta", {}).get("fallback_diversion", 0.30))


def get_params(road, overrides=None):
    """Merged params for a road, or None if the road has no entry (-> 0.30 fallback)."""
    cfg = _load()
    roads = cfg.get("roads", {})
    if road not in roads:
        return None
    meta = cfg.get("_meta", {})
    p = dict(cfg.get("default", {}))
    p.update(roads[road])
    p["beta"] = meta.get("beta", 4.0)
    p["pcu"] = {k: v for k, v in meta.get("pcu", {}).items() if k != "pcu_source"}
    if overrides:
        p.update(overrides)          # used by the sensitivity panel
        if "pcu_bikes" in overrides:
            p["pcu"] = dict(p["pcu"], bikes=overrides["pcu_bikes"])
    return p


def calibrate_alpha(peak_ratio, peak_vc, beta):
    return (peak_ratio - 1.0) / (peak_vc ** beta)


def bpr_time(t0, vc, alpha, beta):
    return t0 * (1.0 + alpha * vc ** beta)


def _sigmoid(x):
    if x >= 0:
        return 1.0 / (1.0 + math.exp(-x))
    e = math.exp(x)
    return e / (1.0 + e)


def _pcu_total(c, pcu):
    return sum(c.get(k, 0.0) * pcu[k] for k in ("bikes", "cars", "buses", "trucks"))


def solve_hour(counts, capacity_pcu, p, max_iter=200, tol=1e-9):
    """counts: dict bikes/cars/buses/trucks for ONE hour. Returns dict of results."""
    pcu, beta = p["pcu"], p["beta"]
    alpha = calibrate_alpha(p["peak_ratio"], p.get("calib_vc", 0.85), beta)
    t0 = p["free_flow_min"]
    t_fly = t0 * p["flyover_time_factor"]
    bikes = float(counts.get("bikes", 0.0))
    total_pcu = _pcu_total(counts, pcu)
    total_veh = sum(float(counts.get(k, 0.0)) for k in ("bikes", "cars", "buses", "trucks"))
    eligible = bikes * p["eligible_share"]
    fly_cap_bikes = p["flyover_capacity_ratio"] * capacity_pcu / pcu["bikes"]

    vc0 = total_pcu / capacity_pcu
    t_before = bpr_time(t0, vc0, alpha, beta)

    d, it = 0.0, 0
    for it in range(1, max_iter + 1):
        vc = (total_pcu - pcu["bikes"] * d) / capacity_pcu
        t_g = bpr_time(t0, vc, alpha, beta)
        prob = _sigmoid(p["logit_k"] * (t_g - t_fly - p["ramp_penalty_min"]))
        d_new = min(eligible * prob, fly_cap_bikes)
        d_next = 0.5 * d + 0.5 * d_new          # damping -> stable convergence
        if abs(d_next - d) <= tol * max(bikes, 1.0):
            d = d_next
            break
        d = d_next
    vc_after = (total_pcu - pcu["bikes"] * d) / capacity_pcu
    t_after = bpr_time(t0, vc_after, alpha, beta)
    t_fly_actual = t_fly + p["ramp_penalty_min"]
    # vehicle-minutes on the stretch, before vs after
    vm_before = total_veh * t_before
    vm_after = (total_veh - d) * t_after + d * t_fly_actual
    return {
        "diversion_rate": d / bikes if bikes > 0 else 0.0,   # share of ALL bikes
        "diverted_bikes": d,
        "vc_before": vc0,
        "vc_after": vc_after,
        "delay_before_min": t_before - t0,
        "delay_after_min": t_after - t0,
        "vehicle_hours_saved": max(vm_before - vm_after, 0.0) / 60.0,
        "iterations": it,
    }


def compute_day(road, hourly_counts, overrides=None):
    """hourly_counts: list of 24 dicts (bikes/cars/buses/trucks) for one day type.
    Returns {'road','fallback','hours':[24 dicts],'summary':{...}}."""
    p = get_params(road, overrides)
    if p is None:
        return {"road": road, "fallback": True,
                "hours": [{"diversion_rate": fallback_diversion()} for _ in hourly_counts],
                "summary": {}}
    peak_pcu = max(_pcu_total(c, p["pcu"]) for c in hourly_counts)
    capacity = peak_pcu / p["peak_vc"] if peak_pcu > 0 else 1.0
    hours = [solve_hour(c, capacity, p) for c in hourly_counts]
    summary = {
        "peak_vc": max(h["vc_before"] for h in hours),
        "peak_diversion": max(h["diversion_rate"] for h in hours),
        "offpeak_diversion": min(h["diversion_rate"] for h in hours),
        "hours_useful": sum(1 for h in hours if h["diversion_rate"] >= 0.10),
        "vehicle_hours_saved_day": sum(h["vehicle_hours_saved"] for h in hours),
    }
    return {"road": road, "fallback": False, "hours": hours, "summary": summary}


def diversion_rate(road, hour, hourly_counts, overrides=None):
    """Single number for /predict. Falls back to 0.30 for unknown roads."""
    return compute_day(road, hourly_counts, overrides)["hours"][hour]["diversion_rate"]
