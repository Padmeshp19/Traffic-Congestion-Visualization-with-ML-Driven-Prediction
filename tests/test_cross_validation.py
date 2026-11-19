import json, os, subprocess, sys
import pytest

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


@pytest.fixture(scope='module')
def cv():
    """Runs the script once (about a minute) and loads the JSON it writes."""
    run = subprocess.run([sys.executable, 'scripts/cross_validate.py'], cwd=ROOT, capture_output=True, text=True)
    assert run.returncode == 0, run.stderr
    with open(os.path.join(ROOT, 'models', 'cross_validation.json')) as f:
        return json.load(f)


def test_fold_counts_and_coverage(cv):
    assert cv['leave_one_day_out']['folds'] == 7 and len(cv['leave_one_day_out']['per_day']) == 7
    assert cv['leave_one_road_out']['folds'] == 22 and len(cv['leave_one_road_out']['per_road']) == 22
    assert sum(d['rows'] for d in cv['leave_one_day_out']['per_day']) == cv['rows']


def test_silk_board_family_is_reported(cv):
    assert len(cv['silk_board_family']) == 7 and 'Silk Board Junction' in cv['silk_board_family']


def test_every_method_is_scored_in_both_tests(cv):
    for key in ('leave_one_day_out', 'leave_one_road_out'):
        kinds = {r['type'] for r in cv[key]['pooled']}
        assert kinds == {'model', 'baseline'}
        assert all(0 < r['mae'] and r['mae_pct_of_mean'] > 0 for r in cv[key]['pooled'])


def test_unseen_road_is_much_harder_than_unseen_day(cv):
    day = next(r for r in cv['leave_one_day_out']['pooled'] if r['name'] == 'Gradient Boosting')
    road = next(r for r in cv['leave_one_road_out']['pooled'] if r['name'].startswith('Gradient Boosting as deployed'))
    assert road['r2'] < day['r2'] - 0.3      # the deployed model cannot generalise to a road it has not seen