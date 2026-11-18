"""
baseline_compare.py - compares the trained models with simple "no-ML" lookup rules.
Run from the project root AFTER train_model.py:   python scripts/baseline_compare.py
Uses the SAME train/test split as train_model.py (20% test, random_state=42, stratified by road).
Target: Total_Vehicles (the model really predicts total volume; the vehicle mix is a fixed constant).
"""
import json
import pickle

import numpy as np
import pandas as pd
from sklearn.metrics import mean_absolute_error, r2_score
from sklearn.model_selection import train_test_split

TARGET = 'Total_Vehicles'
TARGET_INDEX = 4                      # Cars, Motorcycles, Buses, Trucks, Total_Vehicles
FEATURES = ['Day_Num', 'Hour', 'Is_Weekend', 'Peak_Flag', 'Road_Encoded']
OUT_PATH = 'models/baseline_comparison.json'

# ---- 1. load data and rebuild the exact split used in train_model.py
df = pd.read_csv('data/training_dataset.csv')
road_mapping = json.load(open('models/road_mapping.json'))
df['Road_Encoded'] = df['Road_Name'].map(road_mapping)

X_train, X_test, y_train, y_test = train_test_split(
    df[FEATURES], df[TARGET], test_size=0.2, random_state=42, stratify=df['Road_Encoded'])
train, test = df.loc[X_train.index], df.loc[X_test.index]


# ---- 2. baseline helpers
def group_mean(keys, fallback=None):
    """Predict each test row with the training mean of its group.
    Groups that never appear in training use `fallback`. Returns (predictions, rows_that_used_fallback)."""
    table = train.groupby(keys)[TARGET].mean().rename('pred')
    pred = test[keys].merge(table, how='left', left_on=keys, right_index=True)['pred']
    pred.index = test.index
    missing = int(pred.isna().sum())
    if fallback is not None:
        pred = pred.fillna(fallback)
    return pred, missing


global_mean = pd.Series(train[TARGET].mean(), index=test.index)
road_mean, _ = group_mean(['Road_Name'])
road_hour, _ = group_mean(['Road_Name', 'Hour'], road_mean)
rwh_global, miss_rwh = group_mean(['Road_Name', 'Is_Weekend', 'Hour'], global_mean)
rwh_road, _ = group_mean(['Road_Name', 'Is_Weekend', 'Hour'], road_mean)
rwh_road_hour, _ = group_mean(['Road_Name', 'Is_Weekend', 'Hour'], road_hour)

baselines = {
    'Global mean': global_mean,
    'Road mean': road_mean,
    'Road + hour': road_hour,
    'Road + weekend + hour, else global mean': rwh_global,
    'Road + weekend + hour, else road mean': rwh_road,
    'Road + weekend + hour, else road + hour': rwh_road_hour,
}

# ---- 3. trained models (column 4 of their 5 outputs = Total_Vehicles)
models = {}
for key, label in (('rf', 'Random Forest'), ('gbm', 'Gradient Boosting')):
    model = pickle.load(open(f'models/{key}_model.pkl', 'rb'))
    models[label] = pd.Series(model.predict(X_test)[:, TARGET_INDEX], index=test.index)


# ---- 4. score everything
def score(pred):
    return r2_score(y_test, pred), mean_absolute_error(y_test, pred)


rows = [(name, *score(p), 'baseline') for name, p in baselines.items()]
rows += [(name, *score(p), 'model') for name, p in models.items()]
rows.sort(key=lambda r: r[1])

# ---- 5. safety check: our rebuilt split must give the same GBM score train_model.py saved
saved = json.load(open('models/model_metrics.json'))['gbm'][TARGET]
gbm_r2, gbm_mae = score(models['Gradient Boosting'])
if abs(gbm_r2 - saved['r2']) > 0.001:
    print(f"WARNING: GBM R2 here is {gbm_r2:.3f} but model_metrics.json says {saved['r2']}. "
          "The split does not match train_model.py, so these numbers are not comparable.\n")

# ---- 6. print the table
print(f'Train rows: {len(train)} | Test rows: {len(test)} | Target: {TARGET}\n')
print(f"{'Method':45s} {'Type':9s} {'R2':>7s} {'MAE':>9s}")
print('-' * 73)
for name, r2, mae, kind in rows:
    print(f'{name:45s} {kind:9s} {r2:7.3f} {mae:9.0f}')

best_name, best_r2, best_mae, _ = max((r for r in rows if r[3] == 'baseline'), key=lambda r: r[1])
print(f'\nStrongest baseline: {best_name} (R2 {best_r2:.3f}, MAE {best_mae:.0f})')
gains = {}
for name, p in models.items():
    r2, mae = score(p)
    gains[name] = {'r2_gain': round(r2 - best_r2, 4), 'mae_change_pct': round(100 * (mae - best_mae) / best_mae, 1)}
    print(f"{name:18s} vs it: {r2 - best_r2:+.3f} R2, MAE {gains[name]['mae_change_pct']:+.1f}%")
print(f'\nNote: {miss_rwh} of {len(test)} test rows have no matching road+weekend+hour cell in training '
      '(almost all are weekend rows), so the fallback rule matters for those.')

# ---- 7. save for the dashboard (models/ is where train_model.py writes its other JSON files)
result = {
    'target': TARGET,
    'train_rows': int(len(train)),
    'test_rows': int(len(test)),
    'rows': [{'name': n, 'type': k, 'r2': round(float(r2), 4), 'mae': round(float(mae), 1)}
             for n, r2, mae, k in sorted(rows, key=lambda r: -r[1])],          # best first
    'strongest_baseline': {'name': best_name, 'r2': round(float(best_r2), 4), 'mae': round(float(best_mae), 1)},
    'gains_vs_strongest_baseline': gains,
    'fallback_rows': miss_rwh,
    'note': 'One random split (20% test, random_state=42, stratified by road). Baselines are lookup averages '
            'computed from the training rows only.',
}
with open(OUT_PATH, 'w') as f:
    json.dump(result, f, indent=2)
print(f'\nSaved {OUT_PATH}')