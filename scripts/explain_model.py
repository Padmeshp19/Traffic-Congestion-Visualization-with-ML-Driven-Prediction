"""
explain_model.py - SHAP explanation of the traffic model (Total_Vehicles output).
Run from the project root AFTER train_model.py:   python scripts/explain_model.py            (Gradient Boosting)
                                                   python scripts/explain_model.py --model rf
Needs: pip install -r requirements-analysis.txt   (shap, matplotlib)

What it does
  * explains every one of the 3,304 road x day x hour slots the models were trained on
  * checks SHAP is exact: base value + the 5 SHAP values must add back up to the model's own prediction
  * reports importance per input AND per group (see GROUPS), because some inputs are copies of others
  * writes models/shap_summary.json plus two pictures in reports/figures/

SHAP describes how the MODEL behaves. It does not prove the model is right and it does not show cause and effect.
"""
import argparse
import json
import os
import pickle

import matplotlib
matplotlib.use('Agg')                       # draw to files, no window needed
import matplotlib.pyplot as plt
import numpy as np
import pandas as pd
import shap

TARGET = 'Total_Vehicles'
TARGET_INDEX = 4                            # Cars, Motorcycles, Buses, Trucks, Total_Vehicles
FEATURES = ['Day_Num', 'Hour', 'Is_Weekend', 'Peak_Flag', 'Road_Encoded']

# Is_Weekend comes from Day_Num and Peak_Flag comes from Hour, so the model can use either copy and SHAP splits
# the credit between them. Adding SHAP values inside a group is allowed (SHAP values are additive) and gives
# the honest total for "time of day", "day of week" and "road".
GROUPS = {
    'Road': ['Road_Encoded'],
    'Time of day': ['Hour', 'Peak_Flag'],
    'Day of week': ['Day_Num', 'Is_Weekend'],
}

parser = argparse.ArgumentParser()
parser.add_argument('--model', choices=['gbm', 'rf'], default='gbm')
args = parser.parse_args()
LABEL = {'gbm': 'Gradient Boosting', 'rf': 'Random Forest'}[args.model]

# ---- 1. load the trained model and the data it was trained on
df = pd.read_csv('data/training_dataset.csv')
road_mapping = json.load(open('models/road_mapping.json'))
df['Road_Encoded'] = df['Road_Name'].map(road_mapping)
X = df[FEATURES]

multi = pickle.load(open(f'models/{args.model}_model.pkl', 'rb'))
model = multi.estimators_[TARGET_INDEX]     # the single model that predicts Total_Vehicles

# ---- 2. SHAP values (TreeExplainer is exact for tree models)
explainer = shap.TreeExplainer(model)
sv = np.asarray(explainer.shap_values(X))   # shape: (3304 rows, 5 inputs)
base = float(np.ravel(explainer.expected_value)[0])

# ---- 3. safety check: base + SHAP values must rebuild the model's prediction
rebuilt = base + sv.sum(axis=1)
worst = float(np.abs(rebuilt - model.predict(X)).max())
assert worst < 1.0, f'SHAP values do not add up to the prediction (largest gap {worst:.2f} vehicles)'

# ---- 4. importance per input and per group (mean absolute SHAP, in vehicles per hour)
idx = {f: i for i, f in enumerate(FEATURES)}
mean_abs = np.abs(sv).mean(axis=0)
feature_rows = sorted(
    ({'feature': f, 'mean_abs_shap': float(mean_abs[idx[f]]),
      'share_pct': float(100 * mean_abs[idx[f]] / mean_abs.sum())} for f in FEATURES),
    key=lambda r: -r['mean_abs_shap'])

group_sv = {g: sv[:, [idx[f] for f in cols]].sum(axis=1) for g, cols in GROUPS.items()}
group_abs = {g: float(np.abs(v).mean()) for g, v in group_sv.items()}
group_total = sum(group_abs.values())
group_rows = sorted(
    ({'group': g, 'features': GROUPS[g], 'mean_abs_shap': group_abs[g],
      'share_pct': 100 * group_abs[g] / group_total} for g in GROUPS),
    key=lambda r: -r['mean_abs_shap'])

# ---- 5. hour effect: combined time-of-day contribution (Hour + Peak_Flag) for each hour of the day
time_sv = group_sv['Time of day']
hour_profile = [float(time_sv[(df['Hour'] == h).to_numpy()].mean()) for h in range(24)]
weekend = df['Is_Weekend'].to_numpy() == 1
day_sv = group_sv['Day of week']
weekend_effect = {'weekday_mean_shap': float(day_sv[~weekend].mean()),
                  'weekend_mean_shap': float(day_sv[weekend].mean())}

# ---- 6. print the results
print(f'{LABEL} | target {TARGET} | {len(df)} slots explained | base value {base:,.0f} vehicles/hour')
print(f'Additivity check passed (largest gap {worst:.4f} vehicles)\n')
print('Importance per input (mean |SHAP|, vehicles per hour):')
for r in feature_rows:
    print(f"  {r['feature']:14s} {r['mean_abs_shap']:9,.0f}  {r['share_pct']:5.1f}%")
print('\nImportance per group (credit shared between copies added together):')
for r in group_rows:
    print(f"  {r['group']:12s} {r['mean_abs_shap']:9,.0f}  {r['share_pct']:5.1f}%   <- {' + '.join(r['features'])}")
top = max(range(24), key=lambda h: hour_profile[h]); low = min(range(24), key=lambda h: hour_profile[h])
print(f'\nHour effect: highest at {top}:00 ({hour_profile[top]:+,.0f}), lowest at {low}:00 ({hour_profile[low]:+,.0f}) vehicles/hour')
print(f"Weekday vs weekend effect: {weekend_effect['weekday_mean_shap']:+,.0f} vs {weekend_effect['weekend_mean_shap']:+,.0f} vehicles/hour")
print('\nRead with care: Road_Encoded is just a label number, so its SHAP value says the road matters but not why.')

# ---- 7. pictures
os.makedirs('reports/figures', exist_ok=True)

fig, ax = plt.subplots(figsize=(7, 3.6))
names = [r['group'] for r in group_rows][::-1]
vals = [r['mean_abs_shap'] for r in group_rows][::-1]
ax.barh(names, vals, color='#2a7de1')
for y, v in enumerate(vals):
    ax.text(v, y, f' {v:,.0f}', va='center', fontsize=9)
ax.set_xlabel('Mean |SHAP| (vehicles per hour)')
ax.set_title(f'What drives the {LABEL} prediction of total traffic')
ax.set_xlim(0, max(vals) * 1.15)
fig.tight_layout(); fig.savefig('reports/figures/shap_importance.png', dpi=160); plt.close(fig)

fig, ax = plt.subplots(figsize=(7.5, 4))
ax.scatter(df['Hour'] + np.random.default_rng(0).uniform(-0.25, 0.25, len(df)), time_sv,
           s=5, alpha=0.25, color='#8fb8ee', label='each road/day slot')
ax.plot(range(24), hour_profile, color='#d62728', lw=2.2, label='average over all slots')
ax.axhline(0, color='grey', lw=0.8)
ax.set_xticks(range(0, 24, 2)); ax.set_xlabel('Hour of day')
ax.set_ylabel('Effect on predicted vehicles per hour')
ax.set_title('Hour effect (Hour + Peak_Flag combined)')
ax.legend(frameon=False)
fig.tight_layout(); fig.savefig('reports/figures/shap_hour.png', dpi=160); plt.close(fig)

# ---- 8. JSON
out = {
    'model': args.model, 'target': TARGET, 'rows_explained': int(len(df)),
    'base_value': base, 'additivity_max_gap': worst,
    'features': feature_rows, 'groups': group_rows,
    'hour_profile': hour_profile, 'weekend_effect': weekend_effect,
    'note': 'SHAP describes how the model behaves on the 22 roads it was trained on. It does not prove the model '
            'is correct. Road_Encoded is an integer label, so its effect is not interpretable beyond "road matters".',
}
with open('models/shap_summary.json', 'w') as f:
    json.dump(out, f, indent=2)
print('\nSaved models/shap_summary.json, reports/figures/shap_importance.png, reports/figures/shap_hour.png')