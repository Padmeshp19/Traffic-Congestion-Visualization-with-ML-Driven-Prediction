import pandas as pd
import numpy as np
import pickle
import json
from sklearn.ensemble import RandomForestRegressor
from sklearn.multioutput import MultiOutputRegressor
from sklearn.model_selection import train_test_split
from sklearn.preprocessing import LabelEncoder
from sklearn.metrics import mean_absolute_error, r2_score

# ---- Load Data ----
df = pd.read_csv('data/merged_ml_ready.csv')

day_order = {
    'Monday':0,'Tuesday':1,'Wednesday':2,
    'Thursday':3,'Friday':4,'Saturday':5,'Sunday':6
}
df['Day_Num'] = df['Day'].map(day_order)

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

# ---- Train Model ----
print("Training model... please wait")
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

# ---- Evaluate ----
y_pred = model.predict(X_test)
print("\n=== MODEL RESULTS ===")
for i, col in enumerate(targets):
    mae = mean_absolute_error(y_test.iloc[:,i], y_pred[:,i])
    r2  = r2_score(y_test.iloc[:,i], y_pred[:,i])
    print(f"{col:20s} → MAE: {mae:8.1f}  |  R²: {r2:.3f}")

# ---- Save Model and Encoder ----
pickle.dump(model,   open('models/traffic_model.pkl', 'wb'))
pickle.dump(le_road, open('models/road_encoder.pkl',  'wb'))

road_mapping = {
    name: int(idx)
    for name, idx in zip(le_road.classes_, le_road.transform(le_road.classes_))
}
with open('models/road_mapping.json', 'w') as f:
    json.dump(road_mapping, f, indent=2)

print("\n✅ Model saved to models/traffic_model.pkl")
print("✅ Encoder saved to models/road_encoder.pkl")
print("✅ Road mapping saved to models/road_mapping.json")

# python app.py