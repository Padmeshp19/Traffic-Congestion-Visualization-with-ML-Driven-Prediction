from flask import Flask, request, jsonify, render_template
import pickle
import json
import numpy as np
import pandas as pd

app = Flask(__name__)

# ---- Load model and encoder ----
model    = pickle.load(open('models/traffic_model.pkl', 'rb'))
le_road  = pickle.load(open('models/road_encoder.pkl',  'rb'))

with open('models/road_mapping.json') as f:
    road_mapping = json.load(f)

with open('models/flyover_impact.json') as f:
    flyover_impact = json.load(f)

# ---- Home route ----
@app.route('/')
def home():
    return render_template('dashboard.html')

# ---- Predict route ----
@app.route('/predict', methods=['POST'])
def predict():
    data = request.json

    road    = data.get('road')
    day_num = int(data.get('day_num'))
    hour    = int(data.get('hour'))
    flyover = data.get('flyover', False)

    # Validate road
    if road not in road_mapping:
        return jsonify({'error': f'Road "{road}" not found'}), 400

    is_weekend = 1 if day_num >= 5 else 0
    peak_flag  = 1 if (7 <= hour <= 10 or 17 <= hour <= 20) else 0
    road_enc   = road_mapping[road]

    features = pd.DataFrame([[day_num, hour, is_weekend, peak_flag, road_enc]],
                             columns=['Day_Num','Hour','Is_Weekend','Peak_Flag','Road_Encoded'])

    pred = model.predict(features)[0]
    pred = np.maximum(0, np.round(pred)).astype(int)

    cars   = int(pred[0])
    bikes  = int(pred[1])
    buses  = int(pred[2])
    trucks = int(pred[3])
    total  = cars + bikes + buses + trucks

    # Apply flyover reduction if toggled on
    if flyover and road in flyover_impact:
        reduction = flyover_impact[road]['congestion_reduction_pct'] / 100
        diverted  = int(bikes * 0.30)
        bikes    -= diverted
        total     = cars + bikes + buses + trucks

    return jsonify({
        'road':      road,
        'day_num':   day_num,
        'hour':      hour,
        'peak_flag': peak_flag,
        'flyover':   flyover,
        'cars':      cars,
        'motorcycles': bikes,
        'buses':     buses,
        'trucks':    trucks,
        'total':     total
    })

# ---- Flyover impact route ----
@app.route('/flyover-impact', methods=['GET'])
def get_flyover_impact():
    return jsonify(flyover_impact)

if __name__ == '__main__':
    app.run(debug=True)