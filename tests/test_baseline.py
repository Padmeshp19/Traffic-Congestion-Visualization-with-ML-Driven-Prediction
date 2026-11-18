import json, os, subprocess, sys
import pytest

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, ROOT)
from app import app


@pytest.fixture(scope='module')
def result():
    """Run the script once from the project root and load the JSON it writes."""
    run = subprocess.run([sys.executable, 'scripts/baseline_compare.py'], cwd=ROOT,
                         capture_output=True, text=True)
    assert run.returncode == 0, run.stderr
    assert 'WARNING' not in run.stdout          # the split must match train_model.py
    with open(os.path.join(ROOT, 'models', 'baseline_comparison.json')) as f:
        return json.load(f)


def test_json_has_models_and_baselines(result):
    kinds = {r['type'] for r in result['rows']}
    assert kinds == {'baseline', 'model'}
    names = {r['name'] for r in result['rows']}
    assert {'Gradient Boosting', 'Random Forest', 'Global mean', 'Road + hour'} <= names
    assert result['strongest_baseline']['name'] in names


def test_gbm_score_matches_train_model(result):
    saved = json.load(open(os.path.join(ROOT, 'models', 'model_metrics.json')))['gbm']['Total_Vehicles']
    gbm = next(r for r in result['rows'] if r['name'] == 'Gradient Boosting')
    assert abs(gbm['r2'] - saved['r2']) < 0.001
    assert abs(gbm['mae'] - saved['mae']) < 1.0


def test_strongest_baseline_is_really_the_best_baseline(result):
    best = max(r['r2'] for r in result['rows'] if r['type'] == 'baseline')
    assert result['strongest_baseline']['r2'] == best


def test_endpoint_serves_the_same_file(result):
    resp = app.test_client().get('/baseline-comparison')
    assert resp.status_code == 200
    assert resp.get_json()['strongest_baseline'] == result['strongest_baseline']