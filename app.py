from flask import Flask, request, jsonify, render_template
import pickle
import json
import os
import numpy as np
import pandas as pd

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

FLYOVER_DIVERSION = 0.30          # share of 2-wheelers diverted onto the flyover (applies to EVERY road)
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
    if flyover:                                   # works for all 22 roads, not just the 7 corridor junctions
        diverted = int(bikes * FLYOVER_DIVERSION)
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
        'bikes_diverted': diverted
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
        pred[:, 1] -= (pred[:, 1] * FLYOVER_DIVERSION).astype(int)
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

if __name__ == '__main__':
    app.run(debug=True)
