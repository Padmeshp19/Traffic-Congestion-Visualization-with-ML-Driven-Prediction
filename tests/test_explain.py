import json, os, subprocess, sys
import pytest

pytest.importorskip('shap')          # skipped if the analysis packages are not installed
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


@pytest.fixture(scope='module')
def summary():
    run = subprocess.run([sys.executable, 'scripts/explain_model.py'], cwd=ROOT,
                         capture_output=True, text=True)
    assert run.returncode == 0, run.stderr
    with open(os.path.join(ROOT, 'models', 'shap_summary.json')) as f:
        return json.load(f)


def test_shap_values_add_up_to_the_prediction(summary):
    assert summary['additivity_max_gap'] < 1.0


def test_all_five_inputs_and_three_groups_reported(summary):
    assert {r['feature'] for r in summary['features']} == {'Day_Num', 'Hour', 'Is_Weekend', 'Peak_Flag', 'Road_Encoded'}
    assert {r['group'] for r in summary['groups']} == {'Road', 'Time of day', 'Day of week'}
    assert abs(sum(r['share_pct'] for r in summary['groups']) - 100) < 0.01


def test_hour_profile_covers_24_hours_and_peaks_in_the_day(summary):
    prof = summary['hour_profile']
    assert len(prof) == 24
    assert prof.index(max(prof)) in range(7, 21)      # busiest hour is not overnight
    assert prof.index(min(prof)) in (22, 23, 0, 1, 2, 3, 4, 5)


def test_pictures_were_written(summary):
    for name in ('shap_importance.png', 'shap_hour.png'):
        assert os.path.getsize(os.path.join(ROOT, 'reports', 'figures', name)) > 5000