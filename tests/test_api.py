import os, sys, pytest
sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
from app import app, road_mapping

@pytest.fixture
def c(): return app.test_client()

def post(c, road, hour, fly, day=4):
    return c.post('/predict', json={'road': road, 'day_num': day, 'hour': hour, 'flyover': fly})

def test_old_keys_kept_and_new_key_added(c):
    j = post(c, 'Hosa Road', 8, True).get_json()
    for k in ['model','road','day_num','hour','peak_flag','flyover','cars','motorcycles','buses','trucks','total','bikes_before','bikes_diverted']:
        assert k in j
    assert 0 <= j['diversion_rate'] <= 0.4

def test_invalid_road(c): assert post(c, 'Nope', 8, True).status_code == 400

def test_all_22_roads_and_flyover_lowers_bikes(c):
    assert len(road_mapping) == 22
    for r in road_mapping:
        a = post(c, r, 8, False).get_json(); b = post(c, r, 8, True).get_json()
        assert b['motorcycles'] <= a['motorcycles']

def test_diversion_varies_by_road_and_hour(c):
    peak = {r: post(c, r, 8, True).get_json()['diversion_rate'] for r in road_mapping}
    assert max(peak.values()) - min(peak.values()) > 0.1
    assert post(c,'Sony World Junction',3,True).get_json()['diversion_rate'] < post(c,'Sony World Junction',8,True).get_json()['diversion_rate']

def test_grid_matches_predict(c):
    g = c.get('/predict-grid?road=Hosa%20Road&flyover=1').get_json()['grid']
    p = post(c, 'Hosa Road', 8, True, day=2).get_json()
    assert abs(g[2][8][1] - p['motorcycles']) <= 1

def test_recommendation_includes_dynamic_benefit_metrics(c):
    r = c.get('/flyover-recommendation?model=gbm&day_num=4')
    assert r.status_code == 200
    j = r.get_json()
    assert j['day_num'] == 4
    assert j['benefit_thresholds']['near_gridlock_vc'] == 0.85
    row = next(x for x in j['roads'] if x['road'] == 'Hosa Road')
    for key in ['peak_vc', 'vehicle_hours_saved', 'useful_hours',
                'peak_diversion_rate', 'verdict', 'site_readiness']:
        assert key in row
    assert row['verdict'] in {'Prioritize', 'Consider', 'Monitor'}

def test_sensitivity_returns_ranges_and_verdict_stability(c):
    r = c.get('/flyover-sensitivity?model=gbm&road=Hosa%20Road&day_num=4&runs=50')
    assert r.status_code == 200
    j = r.get_json()
    assert j['runs'] == 50
    assert len(j['p10_p90']['vehicle_hours_saved']) == 2
    assert 0 <= j['verdict_holds_pct'] <= 100
    assert len(j['tornado']) >= 4
