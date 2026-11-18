from flask import Flask, request, jsonify, render_template
import pickle
import json
import os
import numpy as np
import pandas as pd
import flyover_model as fm          # dynamic, congestion-driven flyover diversion

app = Flask(__name__)

# ---- Load models and encoder ----
# train_model.py saves the true Random Forest as rf_model.pkl and the Gradient Boosting model as gbm_model.pkl
# (traffic_model.pkl is a copy of the GBM model, kept only for older scripts that expect that filename).
MODELS = {}
if os.path.exists('models/rf_model.pkl'):
    MODELS['rf'] = pickle.load(open('models/rf_model.pkl', 'rb'))
if os.path.exists('models/gbm_model.pkl'):
    MODELS['gbm'] = pickle.load(open('models/gbm_model.pkl', 'rb'))
if not MODELS:   # fall back for older training runs that only wrote traffic_model.pkl
    MODELS['rf'] = pickle.load(open('models/traffic_model.pkl', 'rb'))
DEFAULT_MODEL = 'gbm' if 'gbm' in MODELS else 'rf'

le_road = pickle.load(open('models/road_encoder.pkl', 'rb'))

with open('models/road_mapping.json') as f:
    road_mapping = json.load(f)

def load_json(path):
    if os.path.exists(path):
        with open(path) as f:
            return json.load(f)
    return {}

FLYOVER_DIVERSION = 0.30          # fallback for roads without calibrated parameters

_DIV_CACHE = {}


def _prediction_rows(model_name, road, day_num):
    rows = [[day_num, hour, 1 if day_num >= 5 else 0,
             1 if (7 <= hour <= 10 or 17 <= hour <= 20) else 0,
             road_mapping[road]] for hour in range(24)]
    features = pd.DataFrame(rows, columns=['Day_Num', 'Hour', 'Is_Weekend',
                                            'Peak_Flag', 'Road_Encoded'])
    predictions = np.maximum(0, np.round(MODELS[model_name].predict(features))).astype(int)
    return [
        {'bikes': int(row[1]), 'cars': int(row[0]),
         'buses': int(row[2]), 'trucks': int(row[3])}
        for row in predictions[:, :4]
    ]


def _day_result(model_name, road, day_num, overrides=None):
    return fm.compute_day(road, _prediction_rows(model_name, road, day_num), overrides)


def day_diversion(model_name, road, day_num):
    """24 hourly diversion rates (share of bikes) for one road and day, from the delay model.
    Falls back to FLYOVER_DIVERSION if the road has no entry in data/flyover_params.json."""
    key = (model_name, road, day_num)
    if key not in _DIV_CACHE:
        _DIV_CACHE[key] = [h['diversion_rate']
                           for h in _day_result(model_name, road, day_num)['hours']]
    return _DIV_CACHE[key]

flyover_impact = load_json('models/flyover_impact.json')
model_metrics  = load_json('models/model_metrics.json')
# ---- Home route ----
@app.route('/')
def home():
    return render_template('dashboard.html')

# ---- Predict route ----
@app.route('/predict', methods=['POST'])
def predict():
    data = request.json or {}

    road       = data.get('road')
    day_num    = int(data.get('day_num'))
    hour       = int(data.get('hour'))
    flyover    = data.get('flyover', False)
    model_name = str(data.get('model', DEFAULT_MODEL)).lower()   # optional -> old callers unaffected

    if model_name not in MODELS:
        return jsonify({'error': f'Model "{model_name}" not found. Available: {sorted(MODELS)}'}), 400

    # Validate road
    if road not in road_mapping:
        return jsonify({'error': f'Road "{road}" not found'}), 400

    is_weekend = 1 if day_num >= 5 else 0
    peak_flag  = 1 if (7 <= hour <= 10 or 17 <= hour <= 20) else 0
    road_enc   = road_mapping[road]

    features = pd.DataFrame([[day_num, hour, is_weekend, peak_flag, road_enc]],
                             columns=['Day_Num','Hour','Is_Weekend','Peak_Flag','Road_Encoded'])

    pred = MODELS[model_name].predict(features)[0]
    pred = np.maximum(0, np.round(pred)).astype(int)

    cars   = int(pred[0])
    bikes  = int(pred[1])
    buses  = int(pred[2])
    trucks = int(pred[3])
    total  = cars + bikes + buses + trucks

    # Apply flyover reduction if toggled on (unchanged logic)
    bikes_before = bikes
    diverted = 0
    diversion_rate = day_diversion(model_name, road, day_num)[hour]
    if flyover:                                   # works for all 22 roads, not just the 7 corridor junctions
        diverted = int(bikes * diversion_rate)
        bikes   -= diverted
        total    = cars + bikes + buses + trucks

    return jsonify({
        'model':     model_name,
        'road':      road,
        'day_num':   day_num,
        'hour':      hour,
        'peak_flag': peak_flag,
        'flyover':   flyover,
        'cars':      cars,
        'motorcycles': bikes,
        'buses':     buses,
        'trucks':    trucks,
        'total':     total,
        'bikes_before':   bikes_before,
        'bikes_diverted': diverted,
        'diversion_rate': round(diversion_rate, 4)   # NEW key; all existing keys unchanged
    })

# ---- Full week grid in ONE request (fast path used by the dashboard) ----
@app.route('/predict-grid', methods=['GET'])
def predict_grid():
    model_name = request.args.get('model', DEFAULT_MODEL).lower()
    road       = request.args.get('road')
    flyover    = request.args.get('flyover', '0').lower() in ('1', 'true')

    if model_name not in MODELS:
        return jsonify({'error': f'Model "{model_name}" not found. Available: {sorted(MODELS)}'}), 400
    if road not in road_mapping:
        return jsonify({'error': f'Road "{road}" not found'}), 400

    rows = [[d, h, 1 if d >= 5 else 0,
             1 if (7 <= h <= 10 or 17 <= h <= 20) else 0,
             road_mapping[road]] for d in range(7) for h in range(24)]
    X = pd.DataFrame(rows, columns=['Day_Num','Hour','Is_Weekend','Peak_Flag','Road_Encoded'])

    pred = np.maximum(0, np.round(MODELS[model_name].predict(X))).astype(int)[:, :4]
    if flyover:                                      # same rule as /predict, all roads
        rates = np.array([r for d in range(7) for r in day_diversion(model_name, road, d)])
        pred[:, 1] -= (pred[:, 1] * rates).astype(int)
    total = pred.sum(axis=1, keepdims=True)
    grid  = np.hstack([pred, total]).reshape(7, 24, 5).tolist()   # [day][hour] = [cars, bikes, buses, trucks, total]

    return jsonify({'model': model_name, 'road': road, 'flyover': flyover, 'grid': grid})

# ---- Flyover impact route ----
@app.route('/flyover-impact', methods=['GET'])
def get_flyover_impact():
    return jsonify(flyover_impact)

# ---- Model comparison route (new) ----
@app.route('/model-metrics', methods=['GET'])
def get_model_metrics():
    return jsonify(model_metrics)

# ---- Baseline comparison route (written by scripts/baseline_compare.py) ----
# Read on every request, so re-running the script updates the dashboard without restarting Flask.
@app.route('/baseline-comparison', methods=['GET'])
def get_baseline_comparison():
    data = load_json('models/baseline_comparison.json')
    if not data:
        return jsonify({'error': 'models/baseline_comparison.json not found. '
                                 'Run: python scripts/baseline_compare.py'}), 404
    return jsonify(data)

# ---- Flyover recommendation: demand (from the models) + site readiness (from a config file) ----
# Additive only: /predict, /predict-grid, /flyover-impact and /model-metrics are untouched, and the
# Unity integration (which uses /predict) is not affected. If the two JSON files are missing the
# defaults below are used, so the server still starts.
JUNCTION_PROFILES = load_json('data/junction_profiles.json').get('roads', {})
RECO_CONFIG = load_json('data/recommendation_config.json')

_TIERS = RECO_CONFIG.get('demand_tiers', {})
HIGH_TIER   = {'avg': float(_TIERS.get('high', {}).get('min_avg_bikes_diverted_per_hr', 1900)),
               'peak': float(_TIERS.get('high', {}).get('min_peak_bikes_diverted_per_hr', 3300))}
MEDIUM_TIER = {'avg': float(_TIERS.get('medium', {}).get('min_avg_bikes_diverted_per_hr', 1500)),
               'peak': float(_TIERS.get('medium', {}).get('min_peak_bikes_diverted_per_hr', 2700))}
SITE_LABELS = RECO_CONFIG.get('site_labels', {
    'compatible_assumed': 'Environment considered feasible (straight road, metro in the median, 2 lanes each side, service road)',
    'unknown': 'Surrounding-environment data not available'})
RECO_RULES = RECO_CONFIG.get('recommendations', {
    'high|compatible_assumed':   {'level': 'recommended',     'text': 'Flyover recommended'},
    'medium|compatible_assumed': {'level': 'second',          'text': 'Flyover recommended (second priority)'},
    'low|compatible_assumed':    {'level': 'not_recommended', 'text': 'Flyover not recommended: 2-wheeler demand is too low to justify it'},
    'high|unknown':              {'level': 'provisional',     'text': 'Provisional verdict: flyover looks justified on 2-wheeler demand'},
    'medium|unknown':            {'level': 'provisional',     'text': 'Provisional verdict: flyover may be justified on 2-wheeler demand (second priority)'},
    'low|unknown':               {'level': 'provisional',     'text': 'Provisional verdict: flyover not justified on 2-wheeler demand alone'},
    'any|unknown':               {'level': 'withheld',        'text': 'Verdict withheld: surrounding-environment data not available'}})
INCONCLUSIVE_NOTE = RECO_CONFIG.get('inconclusive_note',
    'This verdict is provisional. It cannot be concluded because surrounding-environment data is not available '
    'for this junction; it can be confirmed once the site data is recorded.')
UNKNOWN_OPEN_ITEMS = RECO_CONFIG.get('unknown_open_items', [])
ENVIRONMENT_NOTE = RECO_CONFIG.get('environment_note',
    'The surrounding environment was considered feasible for this junction until site data is recorded. '
    'It cannot be guaranteed that it will remain the same once the site is surveyed.')
RECO_NOTES = RECO_CONFIG.get('data_notes', [])
_RECO_CACHE = {}

def _demand_tier(avg_diverted, peak_diverted):
    # Both the average and the peak-hour demand must reach the tier's minimum.
    if avg_diverted >= HIGH_TIER['avg'] and peak_diverted >= HIGH_TIER['peak']:
        return 'high'
    if avg_diverted >= MEDIUM_TIER['avg'] and peak_diverted >= MEDIUM_TIER['peak']:
        return 'medium'
    return 'low'

def _recommendation_for(tier, site_status):
    # A road without site data never gets a "candidate" verdict.
    # Roads without environment data get a PROVISIONAL verdict (demand only), flagged as not conclusive.
    withheld = RECO_RULES.get('any|unknown', {'level': 'withheld', 'text': 'Verdict withheld: surrounding-environment data not available'})
    if site_status == 'compatible_assumed':
        return RECO_RULES.get(f'{tier}|{site_status}', withheld)
    return RECO_RULES.get(f'{tier}|unknown', withheld)

def _road_demand(model_name, road):
    """Demand metrics for one road over all 168 day/hour slots (same prediction rule as /predict-grid)."""
    rows = [_prediction_rows(model_name, road, d) for d in range(7)]
    diverted = np.array([
        row['bikes'] * hour['diversion_rate']
        for day_num, day in enumerate(rows)
        for row, hour in zip(day, _day_result(model_name, road, day_num)['hours'])
    ])
    totals = np.array([
        row['bikes'] + row['cars'] + row['buses'] + row['trucks']
        for day in rows for row in day
    ])
    peak = np.array([
        h in (7, 8, 9, 10, 17, 18, 19, 20)
        for d in range(7) for h in range(24)
    ])
    return {
        'avg_total_per_hr': int(round(totals.mean())),
        'avg_bikes_diverted_per_hr': int(round(diverted.mean())),
        'peak_bikes_diverted_per_hr': int(round(diverted[peak].mean())),
        'bike_share_pct': round(100 * sum(row['bikes'] for day in rows for row in day) / max(1, totals.sum()), 1),
        'reduction_pct': round(100 * diverted.sum() / max(1, totals.sum()), 1),
    }


def _benefit_metrics(model_name, road, day_num):
    return _benefit_metrics_with_overrides(model_name, road, day_num)


def _benefit_metrics_with_overrides(model_name, road, day_num, overrides=None):
    result = _day_result(model_name, road, day_num, overrides)
    summary = result.get('summary', {})
    return {
        'peak_vc': round(float(summary.get('peak_vc', 0.0)), 3),
        'vehicle_hours_saved': round(float(summary.get('vehicle_hours_saved_day', 0.0)), 2),
        'useful_hours': int(summary.get('hours_useful', 0)),
        'peak_diversion_rate': round(float(summary.get('peak_diversion', 0.0)), 4),
        'fallback': bool(result.get('fallback', False)),
    }


def _benefit_verdict(metrics):
    if metrics['peak_vc'] >= 0.85:
        readiness = 'Near-gridlock'
    elif metrics['peak_vc'] >= 0.65:
        readiness = 'Heavy'
    else:
        readiness = 'Moderate'

    if (metrics['vehicle_hours_saved'] >= 1.0 and
            metrics['useful_hours'] >= 2 and metrics['peak_vc'] >= 0.85):
        verdict = 'Prioritize'
    elif (metrics['vehicle_hours_saved'] > 0 and
          metrics['useful_hours'] >= 1 and metrics['peak_vc'] >= 0.65):
        verdict = 'Consider'
    else:
        verdict = 'Monitor'
    return {'verdict': verdict, 'site_readiness': readiness}


def _build_recommendations(model_name, day_num=4):
    roads = []
    for road in road_mapping:
        row = {'road': road}
        row.update(_road_demand(model_name, road))
        profile = JUNCTION_PROFILES.get(road, {})
        site_status = profile.get('site_status', 'unknown')
        row['demand_tier'] = _demand_tier(row['avg_bikes_diverted_per_hr'], row['peak_bikes_diverted_per_hr'])
        row['site_status'] = site_status
        row['site_label'] = SITE_LABELS.get(site_status, SITE_LABELS.get('unknown', 'Site data not available'))
        row['site_notes'] = profile.get('notes', '')
        row['open_items'] = profile.get('open_items', [])
        reco = _recommendation_for(row['demand_tier'], site_status)
        row['recommendation'] = reco['text']
        row['recommendation_level'] = reco['level']
        row['conclusive'] = site_status == 'compatible_assumed'
        benefit = _benefit_metrics(model_name, road, day_num)
        row.update(benefit)
        row.update(_benefit_verdict(benefit))
        if site_status == 'compatible_assumed':
            benefit_rules = {
                'Prioritize': ('recommended', 'Flyover recommended'),
                'Consider': ('second', 'Flyover recommended (second priority)'),
                'Monitor': ('not_recommended', 'Flyover not recommended: modelled benefit is limited'),
            }
            row['recommendation_level'], row['recommendation'] = benefit_rules[row['verdict']]
        elif row['verdict'] == 'Monitor':
            row['recommendation_level'] = 'provisional'
            row['recommendation'] = 'Provisional verdict: modelled flyover benefit is limited'
        if not row['open_items'] and not row['conclusive']:
            row['open_items'] = UNKNOWN_OPEN_ITEMS
        roads.append(row)
    roads.sort(key=lambda r: r['avg_bikes_diverted_per_hr'], reverse=True)
    for i, r in enumerate(roads, start=1):
        r['demand_rank'] = i
        r['roads_ranked'] = len(roads)
    return roads

@app.route('/flyover-recommendation', methods=['GET'])
def flyover_recommendation():
    model_name = request.args.get('model', DEFAULT_MODEL).lower()
    day_num = int(request.args.get('day_num', 4))
    if model_name not in MODELS:
        return jsonify({'error': f'Model "{model_name}" not found. Available: {sorted(MODELS)}'}), 400
    if not 0 <= day_num <= 6:
        return jsonify({'error': 'day_num must be between 0 and 6'}), 400
    cache_key = (model_name, day_num)
    if cache_key not in _RECO_CACHE:
        _RECO_CACHE[cache_key] = _build_recommendations(model_name, day_num)
    return jsonify({
        'model': model_name,
        'day_num': day_num,
        'diversion': FLYOVER_DIVERSION,
        'thresholds': {'high': HIGH_TIER, 'medium': MEDIUM_TIER},
        'benefit_thresholds': {
            'near_gridlock_vc': 0.85,
            'heavy_vc': 0.65,
            'min_saved_vehicle_hours': 1.0,
            'min_useful_hours': 2,
        },
        'environment_note': ENVIRONMENT_NOTE,
        'inconclusive_note': INCONCLUSIVE_NOTE,
        'data_notes': RECO_NOTES,
        'roads': _RECO_CACHE[cache_key]
    })


@app.route('/flyover-sensitivity', methods=['GET'])
def flyover_sensitivity():
    model_name = request.args.get('model', DEFAULT_MODEL).lower()
    road = request.args.get('road')
    day_num = int(request.args.get('day_num', 4))
    runs = min(max(int(request.args.get('runs', 240)), 50), 500)
    if model_name not in MODELS:
        return jsonify({'error': f'Model "{model_name}" not found. Available: {sorted(MODELS)}'}), 400
    if road not in road_mapping:
        return jsonify({'error': f'Road "{road}" not found'}), 400
    if not 0 <= day_num <= 6:
        return jsonify({'error': 'day_num must be between 0 and 6'}), 400

    rng = np.random.default_rng(42)
    fields = {
        'beta': (2.8, 5.2, 'BPR beta'),
        'eligible_share': (0.28, 0.52, 'Eligible bike share'),
        'ramp_penalty_min': (1.05, 1.95, 'Ramp penalty'),
        'flyover_capacity_ratio': (0.21, 0.39, 'Flyover capacity'),
        'pcu_bikes': (0.4, 0.6, 'Bike PCU'),
        'logit_k': (1.4, 2.6, 'Choice-model slope'),
    }
    baseline = _benefit_metrics(model_name, road, day_num)
    baseline.update(_benefit_verdict(baseline))
    samples = []
    for _ in range(runs):
        overrides = {field: rng.uniform(low, high)
                     for field, (low, high, _) in fields.items()}
        metrics = _benefit_metrics_with_overrides(model_name, road, day_num, overrides)
        metrics.update(_benefit_verdict(metrics))
        samples.append(metrics)

    def percentile(key, value):
        return round(float(np.percentile([sample[key] for sample in samples], value)), 2)

    tornado = []
    base_values = {
        'beta': 4.0, 'eligible_share': 0.4, 'ramp_penalty_min': 1.5,
        'flyover_capacity_ratio': 0.3, 'pcu_bikes': 0.5, 'logit_k': 2.0,
    }
    for field, (_, _, label) in fields.items():
        results = []
        for factor in (0.7, 1.3):
            metrics = _benefit_metrics_with_overrides(
                model_name, road, day_num, {field: base_values[field] * factor})
            results.append(metrics['vehicle_hours_saved'])
        tornado.append({'parameter': field, 'label': label,
                        'impact_hours': round(abs(results[1] - results[0]) / 2, 2)})
    tornado.sort(key=lambda item: item['impact_hours'], reverse=True)

    return jsonify({
        'model': model_name, 'road': road, 'day_num': day_num, 'runs': runs,
        'baseline': baseline,
        'p10_p90': {
            'vehicle_hours_saved': [percentile('vehicle_hours_saved', 10), percentile('vehicle_hours_saved', 90)],
            'peak_diversion_rate': [percentile('peak_diversion_rate', 10), percentile('peak_diversion_rate', 90)],
            'useful_hours': [percentile('useful_hours', 10), percentile('useful_hours', 90)],
        },
        'verdict_holds_pct': round(100 * sum(
            sample['verdict'] == baseline['verdict'] for sample in samples
        ) / len(samples), 1),
        'tornado': tornado,
    })


# ---- Dashboard -> Unity selection sync (new, additive) ----
# The dashboard POSTs its current selection here; Unity polls it with GET.
_DASH_SELECTION = {'road': None, 'day_num': None, 'hour': None,
                   'model': DEFAULT_MODEL, 'flyover': False, 'seq': 0}

@app.route('/dashboard-selection', methods=['GET', 'POST'])
def dashboard_selection():
    if request.method == 'POST':
        d = request.get_json(silent=True) or {}
        road = d.get('road')
        model_name = str(d.get('model', DEFAULT_MODEL)).lower()
        if road not in road_mapping or model_name not in MODELS:
            return jsonify({'error': 'unknown road or model'}), 400
        _DASH_SELECTION.update(
            road=road,
            day_num=max(0, min(6, int(d.get('day_num', 0)))),
            hour=max(0, min(23, int(d.get('hour', 0)))),
            model=model_name,
            flyover=bool(d.get('flyover', False)),
            seq=_DASH_SELECTION['seq'] + 1)
    return jsonify(_DASH_SELECTION)

if __name__ == '__main__':
    app.run(debug=True)
