from flask import Flask, request, jsonify, render_template
import pickle
import json
import os
from functools import lru_cache
import numpy as np
import pandas as pd
from flyover_model import compute_day, load_params, recommendation

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

flyover_impact = load_json('models/flyover_impact.json')
model_metrics  = load_json('models/model_metrics.json')
flyover_params = load_params()


def _prediction_rows(model_name, road, day_num):
    rows = [[day_num, hour, 1 if day_num >= 5 else 0,
             1 if (7 <= hour <= 10 or 17 <= hour <= 20) else 0,
             road_mapping[road]] for hour in range(24)]
    features = pd.DataFrame(rows, columns=['Day_Num', 'Hour', 'Is_Weekend',
                                            'Peak_Flag', 'Road_Encoded'])
    predictions = np.maximum(0, np.round(MODELS[model_name].predict(features))).astype(int)
    return [
        {'cars': int(row[0]), 'motorcycles': int(row[1]),
         'buses': int(row[2]), 'trucks': int(row[3])}
        for row in predictions[:, :4]
    ]


@lru_cache(maxsize=256)
def day_analysis(model_name, road, day_num):
    return compute_day(
        _prediction_rows(model_name, road, day_num),
        road=road,
        params=flyover_params,
    )


def _dynamic_slot(model_name, road, day_num, hour):
    return day_analysis(model_name, road, day_num)['hours'][hour]


def _dynamic_impact():
    impact = {}
    for road in road_mapping:
        analyses = [day_analysis(DEFAULT_MODEL, road, day) for day in range(7)]
        counts = [_prediction_rows(DEFAULT_MODEL, road, day) for day in range(7)]
        avg_bikes = np.mean([row['motorcycles'] for rows in counts for row in rows])
        avg_total = np.mean([
            row['cars'] + row['motorcycles'] + row['buses'] + row['trucks']
            for rows in counts for row in rows
        ])
        diverted = np.mean([
            item['diverted'] for result in analyses for item in result['hours']
        ])
        impact[road] = {
            'avg_motorcycles': int(round(avg_bikes)),
            'avg_total': int(round(avg_total)),
            'bikes_diverted': int(round(diverted)),
            'congestion_reduction_pct': round(100 * diverted / avg_total, 1) if avg_total else 0.0,
        }
    return impact
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

    slot = _dynamic_slot(model_name, road, day_num, hour)
    bikes_before = bikes
    diverted = slot['diverted'] if flyover else 0
    diversion_rate = slot['diversion_rate'] if flyover else 0.0
    if flyover:
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
        'diversion_rate': diversion_rate
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

    grid = []
    for day in range(7):
        rows = _prediction_rows(model_name, road, day)
        if flyover:
            analysis = day_analysis(model_name, road, day)
            for row, slot in zip(rows, analysis['hours']):
                row['motorcycles'] = max(0, row['motorcycles'] - slot['diverted'])
        grid.append([
            [row['cars'], row['motorcycles'], row['buses'], row['trucks'],
             row['cars'] + row['motorcycles'] + row['buses'] + row['trucks']]
            for row in rows
        ])

    return jsonify({'model': model_name, 'road': road, 'flyover': flyover, 'grid': grid})

# ---- Flyover impact route ----
@app.route('/flyover-impact', methods=['GET'])
def get_flyover_impact():
    return jsonify(_dynamic_impact() if flyover_params else flyover_impact)


@app.route('/flyover-verdict', methods=['GET'])
def get_flyover_verdict():
    model_name = request.args.get('model', DEFAULT_MODEL).lower()
    day_num = int(request.args.get('day_num', 4))
    selected_road = request.args.get('road')
    if model_name not in MODELS:
        return jsonify({'error': f'Model "{model_name}" not found. Available: {sorted(MODELS)}'}), 400
    if not 0 <= day_num <= 6:
        return jsonify({'error': 'day_num must be between 0 and 6'}), 400

    ranking = []
    for road in sorted(road_mapping):
        summary = day_analysis(model_name, road, day_num)
        result = {
            'road': road,
            'peak_vc': round(summary['peak_vc'], 3),
            'vehicle_hours_saved': round(summary['vehicle_hours_saved'], 2),
            'useful_hours': summary['useful_hours'],
            'peak_diversion_rate': round(summary['peak_diversion_rate'], 3),
            **recommendation(summary),
        }
        ranking.append(result)
    ranking.sort(key=lambda item: (-item['vehicle_hours_saved'], -item['peak_vc'], item['road']))
    for index, item in enumerate(ranking, 1):
        item['rank'] = index
    selected = next((item for item in ranking if item['road'] == selected_road), None)
    return jsonify({'model': model_name, 'day_num': day_num, 'selected': selected, 'ranking': ranking})


@app.route('/flyover-sensitivity', methods=['GET'])
def get_flyover_sensitivity():
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
    fields = ['beta', 'eligible_share', 'ramp_penalty', 'flyover_ratio', 'bike_pcu', 'logit_k']
    baseline = day_analysis(model_name, road, day_num)
    baseline_rec = recommendation(baseline)
    samples = []
    for _ in range(runs):
        overrides = {
            'beta': rng.uniform(2.8, 5.2),
            'eligible_share': rng.uniform(0.28, 0.52),
            'ramp_penalty': rng.uniform(1.05, 1.95),
            'flyover_ratio': rng.uniform(0.21, 0.39),
            'bike_pcu': rng.uniform(0.4, 0.6),
            'logit_k': rng.uniform(1.4, 2.6),
        }
        result = compute_day(_prediction_rows(model_name, road, day_num),
                             road=road, params=flyover_params, overrides=overrides)
        samples.append({
            'vehicle_hours_saved': result['vehicle_hours_saved'],
            'peak_diversion_rate': result['peak_diversion_rate'],
            'useful_hours': result['useful_hours'],
            'verdict': recommendation(result)['verdict'],
        })

    def percentile(key, value):
        return round(float(np.percentile([sample[key] for sample in samples], value)), 2)

    tornado = []
    for field in fields:
        effects = []
        for factor in (0.7, 1.3):
            base_value = {
                'beta': 4.0, 'eligible_share': 0.4, 'ramp_penalty': 1.5,
                'flyover_ratio': 0.3, 'bike_pcu': 0.5, 'logit_k': 2.0,
            }[field]
            overrides = {field: base_value * factor}
            result = compute_day(_prediction_rows(model_name, road, day_num),
                                 road=road, params=flyover_params, overrides=overrides)
            effects.append(result['vehicle_hours_saved'])
        tornado.append({'parameter': field, 'impact_hours': round(abs(effects[1] - effects[0]) / 2, 2)})
    tornado.sort(key=lambda item: item['impact_hours'], reverse=True)

    return jsonify({
        'model': model_name, 'road': road, 'day_num': day_num, 'runs': runs,
        'baseline': {
            'vehicle_hours_saved': round(baseline['vehicle_hours_saved'], 2),
            'peak_diversion_rate': round(baseline['peak_diversion_rate'], 3),
            'useful_hours': baseline['useful_hours'],
            **baseline_rec,
        },
        'p10_p90': {
            'vehicle_hours_saved': [percentile('vehicle_hours_saved', 10), percentile('vehicle_hours_saved', 90)],
            'peak_diversion_rate': [percentile('peak_diversion_rate', 10), percentile('peak_diversion_rate', 90)],
            'useful_hours': [percentile('useful_hours', 10), percentile('useful_hours', 90)],
        },
        'verdict_holds_pct': round(100 * sum(
            sample['verdict'] == baseline_rec['verdict'] for sample in samples
        ) / len(samples), 1),
        'tornado': tornado,
    })

# ---- Model comparison route (new) ----
@app.route('/model-metrics', methods=['GET'])
def get_model_metrics():
    return jsonify(model_metrics)

if __name__ == '__main__':
    app.run(debug=True)
