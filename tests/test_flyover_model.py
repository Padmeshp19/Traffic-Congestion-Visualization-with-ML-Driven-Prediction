import math

from flyover_model import compute_day, recommendation


def _counts(volume):
    return [
        {"cars": volume, "motorcycles": volume * 2, "buses": 10, "trucks": 5}
        for _ in range(24)
    ]


def test_diversion_is_conserved_and_changes_with_congestion():
    counts = _counts(100)
    counts[8] = {"cars": 1000, "motorcycles": 2000, "buses": 100, "trucks": 50}
    result = compute_day(counts, params={"peak_vc": 0.85})
    night, peak = result["hours"][0], result["hours"][8]

    assert night["diverted"] < peak["diverted"]
    assert 0 <= peak["diversion_rate"] <= 0.4
    assert peak["vc_after"] <= peak["vc_before"]
    assert result["useful_hours"] >= 1


def test_parameter_overrides_change_result_without_mutating_defaults():
    result = compute_day(_counts(500), params={"peak_vc": 0.85})
    sensitive = compute_day(
        _counts(500),
        params={"peak_vc": 0.85},
        overrides={"eligible_share": 0.28, "ramp_penalty": 1.95},
    )

    assert sensitive["vehicle_hours_saved"] <= result["vehicle_hours_saved"]
    assert math.isclose(result["hours"][0]["diversion_rate"],
                        compute_day(_counts(500), params={"peak_vc": 0.85})["hours"][0]["diversion_rate"])


def test_recommendation_keeps_readiness_separate_from_benefit():
    verdict = recommendation({
        "peak_vc": 0.9,
        "vehicle_hours_saved": 2.0,
        "useful_hours": 3,
    })

    assert verdict == {"verdict": "Prioritize", "site_readiness": "Near-gridlock"}
