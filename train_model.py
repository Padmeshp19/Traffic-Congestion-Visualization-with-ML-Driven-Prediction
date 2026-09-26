import pandas as pd
import numpy as np
import pickle
import json
from sklearn.ensemble import RandomForestRegressor, GradientBoostingRegressor
from sklearn.multioutput import MultiOutputRegressor
from sklearn.model_selection import train_test_split
from sklearn.preprocessing import LabelEncoder
from sklearn.metrics import mean_absolute_error, r2_score

# ---- Load Data ----
df = pd.read_csv('data/merged_ml_ready.csv')  # cleaned (see prepare_dataset.py for how)

day_order = {
    'Monday':0,'Tuesday':1,'Wednesday':2,
    'Thursday':3,'Friday':4,'Saturday':5,'Sunday':6
}
df['Day_Num'] = df['Day'].map(day_order)

# ---- Use Synthetic data only for training ----
df = df[df['Source'] == 'Synthetic_Generated'].copy()

# ---- Aggregate to reduce noise ----
df_agg = df.groupby(
    ['Road_Name','Day_Num','Hour','Is_Weekend','Peak_Flag']
)[['Cars','Motorcycles','Buses','Trucks','Total_Vehicles']].mean().round().reset_index()

# ---- Encode road names ----
le_road = LabelEncoder()
df_agg['Road_Encoded'] = le_road.fit_transform(df_agg['Road_Name'])

print("Roads in model:", list(le_road.classes_))

# ---- Features and Targets ----
features = ['Day_Num', 'Hour', 'Is_Weekend', 'Peak_Flag', 'Road_Encoded']
targets  = ['Cars', 'Motorcycles', 'Buses', 'Trucks', 'Total_Vehicles']

X = df_agg[features]
y = df_agg[targets]

# ---- Train Test Split ----
X_train, X_test, y_train, y_test = train_test_split(
    X, y, test_size=0.2, random_state=42
)

# ---- Train Random Forest ----
print("Training Random Forest... please wait")
model = MultiOutputRegressor(
    RandomForestRegressor(
        n_estimators=300,
        max_depth=15,
        min_samples_leaf=2,
        random_state=42,
        n_jobs=-1
    )
)
model.fit(X_train, y_train)

y_pred = model.predict(X_test)
print("\n=== RANDOM FOREST RESULTS ===")
rf_scores = {}
for i, col in enumerate(targets):
    mae = mean_absolute_error(y_test.iloc[:,i], y_pred[:,i])
    r2  = r2_score(y_test.iloc[:,i], y_pred[:,i])
    rf_scores[col] = {'mae': mae, 'r2': r2}
    print(f"{col:20s} -> MAE: {mae:8.1f}  |  R2: {r2:.3f}")

# ---- Train Gradient Boosting ----
print("\nTraining Gradient Boosting model... please wait")
gbm_model = MultiOutputRegressor(
    GradientBoostingRegressor(
        n_estimators=300,
        max_depth=4,
        learning_rate=0.05,
        random_state=42
    )
)
gbm_model.fit(X_train, y_train)

gbm_pred = gbm_model.predict(X_test)
print("\n=== GRADIENT BOOSTING RESULTS ===")
gbm_scores = {}
for i, col in enumerate(targets):
    mae = mean_absolute_error(y_test.iloc[:,i], gbm_pred[:,i])
    r2  = r2_score(y_test.iloc[:,i], gbm_pred[:,i])
    gbm_scores[col] = {'mae': mae, 'r2': r2}
    print(f"{col:20s} -> MAE: {mae:8.1f}  |  R2: {r2:.3f}")

# ---- Side-by-side comparison ----
print("\n=== RF vs GBM COMPARISON ===")
print(f"{'Target':20s} {'RF MAE':>10s} {'GBM MAE':>10s} {'RF R2':>8s} {'GBM R2':>8s}  Better")
for col in targets:
    rf_mae,  rf_r2  = rf_scores[col]['mae'],  rf_scores[col]['r2']
    gbm_mae, gbm_r2 = gbm_scores[col]['mae'], gbm_scores[col]['r2']
    better = 'GBM' if gbm_r2 > rf_r2 else 'RF'
    print(f"{col:20s} {rf_mae:10.1f} {gbm_mae:10.1f} {rf_r2:8.3f} {gbm_r2:8.3f}  {better}")

# ---- Save both models under distinct names (traffic_model.pkl kept for backward compatibility) ----
pickle.dump(gbm_model, open('models/traffic_model.pkl', 'wb'))   # GBM is the better performer, kept as the legacy default file
pickle.dump(gbm_model, open('models/gbm_model.pkl',     'wb'))
pickle.dump(model,     open('models/rf_model.pkl',      'wb'))
pickle.dump(le_road,   open('models/road_encoder.pkl',  'wb'))

road_mapping = {
    name: int(idx)
    for name, idx in zip(le_road.classes_, le_road.transform(le_road.classes_))
}
with open('models/road_mapping.json', 'w') as f:
    json.dump(road_mapping, f, indent=2)

print("\n✅ traffic_model.pkl  — GBM saved as primary model")
print("✅ gbm_model.pkl      — GBM saved separately")
print("✅ rf_model.pkl       — RF saved separately")
print("✅ road_encoder.pkl   — Encoder saved")
print("✅ road_mapping.json  — Road mapping saved")

# ---- Save metrics for the dashboard's /model-metrics route ----
# Dashboard reads metrics['rf'][col] / metrics['gbm'][col], plus train_rows/test_rows for the sample-size line
metrics_out = {
    'rf':  {col: {'mae': round(rf_scores[col]['mae'], 1),  'r2': round(rf_scores[col]['r2'], 3)}  for col in targets},
    'gbm': {col: {'mae': round(gbm_scores[col]['mae'], 1), 'r2': round(gbm_scores[col]['r2'], 3)} for col in targets},
    'train_rows': int(len(X_train)),
    'test_rows':  int(len(X_test)),
}
with open('models/model_metrics.json', 'w') as f:
    json.dump(metrics_out, f, indent=2)
print("✅ model_metrics.json — RF vs GBM comparison saved")
