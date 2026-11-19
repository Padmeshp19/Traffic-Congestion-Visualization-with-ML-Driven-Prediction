"""
cross_validate.py - stricter tests than the single random split used in train_model.py.
Run from the project root AFTER train_model.py:   python scripts/cross_validate.py     (takes about a minute)

Two tests, target Total_Vehicles, models retrained from scratch for every fold with the SAME settings as train_model.py:
  1. Leave-one-day-out (7 folds):  train on 6 days, predict the 7th.  Asks: does it work on a day it has not seen?
  2. Leave-one-road-out (22 folds): train on 21 roads, predict the 22nd. Asks: does it work on a road it has not seen?
     (Expected to fail for the deployed model: Road_Encoded is only a label number, so an unseen road has no meaning.)

Every model is compared with simple "no-ML" rules in the same folds. Writes models/cross_validation.json.
"""
import json

import numpy as np
import pandas as pd
from sklearn.ensemble import GradientBoostingRegressor, RandomForestRegressor
from sklearn.metrics import mean_absolute_error, r2_score

TARGET = 'Total_Vehicles'
FEATURES = ['Day_Num', 'Hour', 'Is_Weekend', 'Peak_Flag', 'Road_Encoded']
NO_ROAD = [f for f in FEATURES if f != 'Road_Encoded']
OUT_PATH = 'models/cross_validation.json'

# ---- 1. data
df = pd.read_csv('data/training_dataset.csv').reset_index(drop=True)
df['Road_Encoded'] = df['Road_Name'].map(json.load(open('models/road_mapping.json')))
roads = sorted(df['Road_Name'].unique())

merged = pd.read_csv('data/merged_ml_ready.csv', usecols=['Source', 'Road_Name', 'Day', 'Day_Num'])
DAY_NAMES = dict(merged[['Day_Num', 'Day']].drop_duplicates().itertuples(index=False))
# Silk Board and the six corridor junctions that train_model.py builds from its weekly profile are NOT independent:
# they share one hourly shape (scaled up or down). Roads in this family are marked so the report can say so.
FAMILY = sorted(set(merged.loc[merged['Source'] == 'Video_Extracted', 'Road_Name']) | {'Silk Board Junction'})


# ---- 2. helpers
def make_gbm():      # same settings as train_model.py
    return GradientBoostingRegressor(n_estimators=300, max_depth=4, learning_rate=0.05, random_state=42)


def make_rf():       # same settings as train_model.py (n_jobs only changes speed, not results)
    return RandomForestRegressor(n_estimators=300, max_depth=15, min_samples_leaf=2, random_state=42, n_jobs=-1)


def fit_predict(make, features, train, test):
    return make().fit(train[features], train[TARGET]).predict(test[features])


def lookup_mean(train, test, keys, fallback):
    """Training mean of each test row's group; `fallback` (array or number) where the group is missing."""
    table = train.groupby(keys)[TARGET].mean().rename('pred')
    pred = test[keys].merge(table, how='left', left_on=keys, right_index=True)['pred'].to_numpy()
    return np.where(np.isnan(pred), fallback, pred)


def scores(y, p):
    return {'r2': float(r2_score(y, p)), 'mae': float(mean_absolute_error(y, p)),
            'mae_pct_of_mean': float(100 * mean_absolute_error(y, p) / y.mean())}


def pooled(preds, kinds):
    """Score the out-of-fold predictions of every method over all rows, best R2 first."""
    assert all(not np.isnan(p).any() for p in preds.values()), 'some rows were never predicted'
    rows = [{'name': n, 'type': kinds[n], **scores(df[TARGET], p)} for n, p in preds.items()]
    return sorted(rows, key=lambda r: -r['r2'])


def show(title, rows):
    print(f'\n{title}')
    print(f"  {'Method':58s} {'R2':>7s} {'MAE':>8s} {'MAE % of mean':>14s}")
    for r in rows:
        print(f"  {r['name']:58s} {r['r2']:7.3f} {r['mae']:8,.0f} {r['mae_pct_of_mean']:13.0f}%")


# ---- 3. leave-one-day-out
GBM, RF, RULE, ROAD_MEAN, GLOBAL = ('Gradient Boosting', 'Random Forest', 'Road + hour (strongest simple rule)',
                                    'Road mean', 'Global mean')
lodo_kinds = {GBM: 'model', RF: 'model', RULE: 'baseline', ROAD_MEAN: 'baseline', GLOBAL: 'baseline'}
lodo = {n: np.full(len(df), np.nan) for n in lodo_kinds}
for day in sorted(df['Day_Num'].unique()):
    te, tr = df[df['Day_Num'] == day], df[df['Day_Num'] != day]
    road_mean = lookup_mean(tr, te, ['Road_Name'], tr[TARGET].mean())
    lodo[GBM][te.index] = fit_predict(make_gbm, FEATURES, tr, te)
    lodo[RF][te.index] = fit_predict(make_rf, FEATURES, tr, te)
    lodo[RULE][te.index] = lookup_mean(tr, te, ['Road_Name', 'Hour'], road_mean)
    lodo[ROAD_MEAN][te.index] = road_mean
    lodo[GLOBAL][te.index] = tr[TARGET].mean()
    print(f'  leave-one-day-out: finished {DAY_NAMES[day]}')

lodo_rows = pooled(lodo, lodo_kinds)
per_day = []
for day in sorted(df['Day_Num'].unique()):
    i = df.index[df['Day_Num'] == day]
    per_day.append({'day_num': int(day), 'day': DAY_NAMES[day], 'rows': int(len(i)),
                    'methods': {n: scores(df.loc[i, TARGET], lodo[n][i]) for n in (GBM, RF, RULE)}})
gbm_day_wins = sum(d['methods'][GBM]['r2'] > d['methods'][RULE]['r2'] for d in per_day)

rule_row = next(r for r in lodo_rows if r['name'] == RULE)
lodo_gain = {}
for name in (GBM, RF):
    r = next(x for x in lodo_rows if x['name'] == name)
    lodo_gain[name] = {'r2_gain': round(r['r2'] - rule_row['r2'], 4),
                       'mae_change_pct': round(100 * (r['mae'] - rule_row['mae']) / rule_row['mae'], 1)}

# ---- 4. leave-one-road-out
DEP_GBM = 'Gradient Boosting as deployed (with road label)'
DEP_RF = 'Random Forest as deployed (with road label)'
NR_GBM = 'Gradient Boosting without the road input'
NR_FAM = 'Gradient Boosting without road input, Silk Board family removed'
HOUR = 'Hour average of the other roads'
loro_kinds = {DEP_GBM: 'model', DEP_RF: 'model', NR_GBM: 'model', NR_FAM: 'model', HOUR: 'baseline', GLOBAL: 'baseline'}
loro = {n: np.full(len(df), np.nan) for n in loro_kinds}
for k, road in enumerate(roads, 1):
    te, tr = df[df['Road_Name'] == road], df[df['Road_Name'] != road]
    tr_nofam = tr[~tr['Road_Name'].isin(FAMILY)] if road in FAMILY else tr   # also hide the road's lookalikes
    loro[DEP_GBM][te.index] = fit_predict(make_gbm, FEATURES, tr, te)
    loro[DEP_RF][te.index] = fit_predict(make_rf, FEATURES, tr, te)
    loro[NR_GBM][te.index] = fit_predict(make_gbm, NO_ROAD, tr, te)
    loro[NR_FAM][te.index] = fit_predict(make_gbm, NO_ROAD, tr_nofam, te)
    loro[HOUR][te.index] = lookup_mean(tr, te, ['Hour'], tr[TARGET].mean())
    loro[GLOBAL][te.index] = tr[TARGET].mean()
    print(f'  leave-one-road-out: finished {k}/{len(roads)} ({road})')

loro_rows = pooled(loro, loro_kinds)
per_road = []
for road in roads:
    i = df.index[df['Road_Name'] == road]
    per_road.append({'road': road, 'avg_total': float(df.loc[i, TARGET].mean()), 'in_silk_board_family': road in FAMILY,
                     'methods': {n: scores(df.loc[i, TARGET], loro[n][i]) for n in (DEP_GBM, NR_GBM, HOUR)}})
positive = {n: sum(r['methods'][n]['r2'] > 0 for r in per_road) for n in (DEP_GBM, NR_GBM, HOUR)}

# ---- 5. print
print('\n' + '=' * 80)
print(f'Rows: {len(df)} | roads: {len(roads)} | Silk Board family (not independent): {", ".join(FAMILY)}')
show('LEAVE-ONE-DAY-OUT (7 folds, all rows predicted by a model that never saw that day)', lodo_rows)
print(f"\n  Gradient Boosting vs strongest simple rule: {lodo_gain[GBM]['r2_gain']:+.3f} R2, MAE {lodo_gain[GBM]['mae_change_pct']:+.1f}%"
      f" | Random Forest: {lodo_gain[RF]['r2_gain']:+.3f} R2, MAE {lodo_gain[RF]['mae_change_pct']:+.1f}%")
print(f'  Gradient Boosting has the higher R2 on {gbm_day_wins} of {len(per_day)} held-out days.')
print(f"\n  {'Held-out day':14s} {'rows':>5s} {'GBM R2':>8s} {'RF R2':>8s} {'Rule R2':>8s}")
for d in per_day:
    m = d['methods']
    print(f"  {d['day']:14s} {d['rows']:5d} {m[GBM]['r2']:8.3f} {m[RF]['r2']:8.3f} {m[RULE]['r2']:8.3f}")

show('LEAVE-ONE-ROAD-OUT (22 folds, every road predicted by a model that never saw that road)', loro_rows)
print(f'\n  Roads where R2 is above zero (22 roads): deployed GBM {positive[DEP_GBM]}, '
      f'GBM without road input {positive[NR_GBM]}, hour average {positive[HOUR]}')
print(f"\n  {'Held-out road':22s} {'avg/h':>7s} | {'deployed GBM':>17s} | {'no road input':>17s} | {'hour average':>17s}")
print(f"  {'':22s} {'':>7s} | {'R2':>7s} {'MAE%':>8s} | {'R2':>7s} {'MAE%':>8s} | {'R2':>7s} {'MAE%':>8s}")
for r in per_road:
    m = r['methods']
    cells = ' | '.join(f"{m[n]['r2']:7.2f} {m[n]['mae_pct_of_mean']:7.0f}%" for n in (DEP_GBM, NR_GBM, HOUR))
    print(f"  {r['road'] + ('*' if r['in_silk_board_family'] else ''):22s} {r['avg_total']:7,.0f} | {cells}")
print('  (* = Silk Board family)')
print('\nRead with care: a very negative R2 on one road mostly means the predicted LEVEL was wrong, because every road')
print('has a different traffic volume and an unseen road gives the model no way to know it. MAE % of mean is easier to read.')

# ---- 6. save for the report / dashboard
out = {
    'target': TARGET, 'rows': int(len(df)), 'silk_board_family': FAMILY,
    'leave_one_day_out': {'folds': len(per_day), 'pooled': lodo_rows, 'per_day': per_day,
                          'gains_vs_strongest_rule': lodo_gain, 'gbm_better_days': int(gbm_day_wins)},
    'leave_one_road_out': {'folds': len(roads), 'pooled': loro_rows, 'per_road': per_road,
                           'roads_with_positive_r2': positive},
    'notes': ['Models are retrained from scratch in every fold with the same settings as train_model.py.',
              'Leave-one-road-out: Road_Encoded is only a label number, so the deployed models cannot predict a new road. '
              'Claim only that the model predicts the 22 roads in the dataset.',
              'Silk Board and six corridor junctions share one hourly shape, so holding out one of them still leaves '
              'lookalikes in training unless the whole family is removed (the "family removed" row).'],
}
with open(OUT_PATH, 'w') as f:
    json.dump(out, f, indent=2)
print(f'\nSaved {OUT_PATH}')