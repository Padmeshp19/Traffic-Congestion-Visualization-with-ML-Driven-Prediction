"""
train_model.py  -  trains RF + GBM on ALL 22 Bangalore roads and writes every artifact app.py needs.

Run from the project root:   python train_model.py
Reads : data/merged_ml_ready.csv, data/calibration_sources.json
Writes: data/training_dataset.csv          (the exact dataset the models are trained on)
        models/{rf_model,gbm_model,traffic_model}.pkl, road_encoder.pkl, road_mapping.json
        models/model_metrics.json, models/flyover_impact.json   (flyover data for ALL roads)
"""
import os, json, pickle
import numpy as np
import pandas as pd
from sklearn.ensemble import RandomForestRegressor, GradientBoostingRegressor
from sklearn.multioutput import MultiOutputRegressor
from sklearn.model_selection import train_test_split
from sklearn.preprocessing import LabelEncoder
from sklearn.metrics import mean_absolute_error, r2_score

os.makedirs('models', exist_ok=True)
FLYOVER_DIVERSION = 0.30          # share of 2-wheelers moved to the flyover (same value app.py uses)
VEHICLES = ['Cars', 'Motorcycles', 'Buses', 'Trucks']
CALIB = json.load(open('data/calibration_sources.json'))
CORRIDOR = ['Electronic City', 'Kudlu Gate', 'HSR Layout', 'Bommanahalli', 'Hosa Road', 'Bommasandra']

# ---------------------------------------------------------------- 1. load
df = pd.read_csv('data/merged_ml_ready.csv')
df['Day_Num'] = df['Day'].map({'Monday':0,'Tuesday':1,'Wednesday':2,'Thursday':3,
                               'Friday':4,'Saturday':5,'Sunday':6})
syn   = df[df['Source'] == 'Synthetic_Generated'].copy()   # area-level, ~10^4 vehicles/hour
video = df[df['Source'] == 'Video_Extracted'].copy()       # junction-level, ~10^2 vehicles/hour

# ---------------------------------------------------------------- 1b. calibrate vehicle mix to published survey
# The synthetic generator used a fixed mix (about 30% cars, 53% motorcycles, 14% buses, 2% trucks) that is not
# documented anywhere. Every road's TOTAL is kept; only the split into the four categories is replaced by the
# published RITES CTTP survey composition (autos counted as trucks, slow-moving vehicles excluded).
def published_mix(calib):
    mapping, locs = calib['category_mapping'], []
    for loc, pct in calib['composition_pct'].items():
        if loc.startswith('_'):
            continue
        kept = {k: v for k, v in pct.items() if k not in mapping['excluded']}
        tot = sum(kept.values())
        locs.append({cat: 100 * sum(kept.get(c, 0) for c in codes) / tot
                     for cat, codes in mapping.items() if cat not in ('_note', 'excluded')})
    mean = {cat: sum(l[cat] for l in locs) / len(locs) for cat in locs[0]}
    return {k: mean[k] / 100 for k in VEHICLES}                      # shares in VEHICLES order
MIX = published_mix(CALIB)
print('Published mix applied:', {k: round(100 * v, 1) for k, v in MIX.items()})

def remix(frame):
    out = frame.copy()
    tot = out['Total_Vehicles'].to_numpy(dtype=float)
    parts = {c: np.round(tot * MIX[c]).astype(int) for c in ('Cars', 'Buses', 'Trucks')}
    out['Cars'], out['Buses'], out['Trucks'] = parts['Cars'], parts['Buses'], parts['Trucks']
    out['Motorcycles'] = np.maximum(0, tot.astype(int) - parts['Cars'] - parts['Buses'] - parts['Trucks'])
    out['Total_Vehicles'] = out[VEHICLES].sum(axis=1)
    return out

syn = remix(syn)          # all 16 synthetic roads now carry the calibrated mix

# ---------------------------------------------------------------- 2. extend to the 6 video-only junctions
# Video rows are tiny junction-level counts and cannot be mixed with area-level data directly
# (that is what broke the model before). The video supplies each junction's RELATIVE size only;
# Silk Board's area-level weekly profile supplies the hourly shape and magnitude, and the published
# survey mix (above) supplies the vehicle split. The video's own mix and hourly shape are reported in
# validate_dataset.py, not used for training.
video_avg = video.groupby('Road_Name')['Total_Vehicles'].mean()
missing = [j for j in CORRIDOR if j not in video_avg.index]
assert not missing, f'No video data for: {missing}'
scales = (0.85 * video_avg[CORRIDOR] / video_avg[CORRIDOR].max()).round(3).to_dict()
print('Junction scale factors (relative to Silk Board):', scales)

silk = syn[syn['Road_Name'] == 'Silk Board Junction']
rng  = np.random.default_rng(42)
parts = []
for junction, scale in scales.items():
    g = silk.copy()
    g['Road_Name'] = junction
    g['Area'] = 'South Bangalore'
    g['Data_Level'] = 'Area_Level'
    g['Source'] = 'Synthetic_Extended'            # clearly marked as derived, not raw
    noise = rng.uniform(0.90, 1.10, size=(len(g), 1))
    g[VEHICLES] = np.maximum(0, np.round(g[VEHICLES].to_numpy() * scale * noise)).astype(int)
    g['Total_Vehicles'] = g[VEHICLES].sum(axis=1)
    parts.append(g)

train_all = pd.concat([syn] + parts, ignore_index=True)

# ---------------------------------------------------------------- 3. aggregate (Road x Day x Hour)
agg = (train_all.groupby(['Road_Name','Day_Num','Hour','Is_Weekend','Peak_Flag'])
                [VEHICLES + ['Total_Vehicles']].mean().round().reset_index())
agg.to_csv('data/training_dataset.csv', index=False)

roads = sorted(agg['Road_Name'].unique())
print(f'Training rows: {len(agg)}  |  roads: {len(roads)}')
assert len(roads) == 22, f'Expected 22 roads, got {len(roads)}: {roads}'

le = LabelEncoder()
agg['Road_Encoded'] = le.fit_transform(agg['Road_Name'])

features = ['Day_Num','Hour','Is_Weekend','Peak_Flag','Road_Encoded']
targets  = VEHICLES + ['Total_Vehicles']
X, y = agg[features], agg[targets]
# stratify on road so every road appears in both train and test
X_tr, X_te, y_tr, y_te = train_test_split(X, y, test_size=0.2, random_state=42, stratify=agg['Road_Encoded'])
print(f'Train: {len(X_tr)} | Test: {len(X_te)}')

def evaluate(name, mdl):
    pred = mdl.predict(X_te)
    out = {}
    print(f'\n=== {name} ===')
    for i, t in enumerate(targets):
        mae, r2 = mean_absolute_error(y_te.iloc[:, i], pred[:, i]), r2_score(y_te.iloc[:, i], pred[:, i])
        out[t] = {'mae': round(mae, 1), 'r2': round(r2, 3)}
        print(f'{t:15s} MAE {mae:9.1f} | R2 {r2:.3f}')
    return out

rf = MultiOutputRegressor(RandomForestRegressor(n_estimators=300, max_depth=15, min_samples_leaf=2,
                                                random_state=42, n_jobs=-1)).fit(X_tr, y_tr)
rf_scores = evaluate('RANDOM FOREST', rf)
gbm = MultiOutputRegressor(GradientBoostingRegressor(n_estimators=300, max_depth=4, learning_rate=0.05,
                                                     random_state=42)).fit(X_tr, y_tr)
gbm_scores = evaluate('GRADIENT BOOSTING', gbm)

# ---------------------------------------------------------------- 4. save models
for path, obj in [('models/gbm_model.pkl', gbm), ('models/traffic_model.pkl', gbm),   # GBM = primary
                  ('models/rf_model.pkl', rf), ('models/road_encoder.pkl', le)]:
    with open(path, 'wb') as f:
        pickle.dump(obj, f)

road_mapping = {n: int(i) for n, i in zip(le.classes_, le.transform(le.classes_))}
json.dump(road_mapping, open('models/road_mapping.json', 'w'), indent=2)
json.dump({'rf': rf_scores, 'gbm': gbm_scores, 'train_rows': int(len(X_tr)), 'test_rows': int(len(X_te)),
           'roads': len(roads)}, open('models/model_metrics.json', 'w'), indent=2)

# ---------------------------------------------------------------- 5. flyover impact for ALL 22 roads
impact = {}
for road, g in agg.groupby('Road_Name'):
    bikes, total = g['Motorcycles'].mean(), g['Total_Vehicles'].mean()
    diverted = int(bikes * FLYOVER_DIVERSION)
    impact[road] = {'avg_motorcycles': int(round(bikes)), 'avg_total': int(round(total)),
                    'bikes_diverted': diverted, 'congestion_reduction_pct': round(100 * diverted / total, 1)}
json.dump(impact, open('models/flyover_impact.json', 'w'), indent=2)

print(f'\nSaved models + flyover_impact.json for {len(impact)} roads')
