import os, sys
sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
import pytest
import flyover_model as fm

SHAPE = [.2,.15,.1,.1,.15,.35,.6,.85,1.0,.9,.7,.65,.65,.65,.7,.75,.85,1.0,.95,.8,.6,.45,.35,.25]

def day(scale=1.0):
    return [{"bikes": 12000*s*scale, "cars": 6000*s*scale, "buses": 400*s*scale, "trucks": 600*s*scale} for s in SHAPE]

def test_unknown_road_falls_back_to_030():
    r = fm.compute_day("Nowhere Rd", day())
    assert r["fallback"] and all(h["diversion_rate"] == 0.30 for h in r["hours"])

def test_never_exceeds_bikes_or_cap():
    for road in ["Hosa Road", "Silk Board Junction", "Jayanagar 4th Block"]:
        for c, h in zip(day(), fm.compute_day(road, day())["hours"]):
            assert 0 <= h["diverted_bikes"] <= c["bikes"] * 0.40 + 1e-6
            assert h["diverted_bikes"] >= 0

def test_rises_with_congestion():
    h = fm.compute_day("Hosa Road", day())["hours"]
    assert h[8]["diversion_rate"] > h[3]["diversion_rate"]
    assert h[8]["vc_before"] > h[3]["vc_before"]

def test_junctions_differ():
    hi = fm.compute_day("Hosa Road", day(), {"peak_vc": 1.05})["summary"]["peak_diversion"]
    lo = fm.compute_day("Hosa Road", day(), {"peak_vc": 0.55})["summary"]["peak_diversion"]
    assert hi > lo + 0.05

def test_converges_and_delay_drops():
    for h in fm.compute_day("Silk Board Junction", day())["hours"]:
        assert h["iterations"] < 200
        assert h["delay_after_min"] <= h["delay_before_min"] + 1e-9
        assert h["vehicle_hours_saved"] >= 0

def test_zero_bikes():
    c = [{"bikes": 0, "cars": 100, "buses": 0, "trucks": 0}] * 24
    assert fm.compute_day("Hosa Road", c)["hours"][0]["diversion_rate"] == 0.0

def test_calibration_hits_peak_ratio():
    a = fm.calibrate_alpha(2.3, 0.85, 4.0)
    assert fm.bpr_time(3.0, 0.85, a, 4.0) == pytest.approx(3.0 * 2.3)

def test_flyover_capacity_cap_binds():
    h = fm.compute_day("Hosa Road", day(), {"flyover_capacity_ratio": 0.01})["hours"]
    base = fm.compute_day("Hosa Road", day())["hours"]
    assert max(x["diversion_rate"] for x in h) < 0.5 * max(x["diversion_rate"] for x in base)
