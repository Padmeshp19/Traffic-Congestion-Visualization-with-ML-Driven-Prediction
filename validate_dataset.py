"""
validate_dataset.py - compares the training dataset with (a) the published RITES CTTP survey and (b) the team's own video.
Run after train_model.py:   python validate_dataset.py
Writes data/validation_report.json and prints the tables used in the report.
None of these checks proves the dataset is measured; they show where it agrees and disagrees with real observations.
"""
import json
import numpy as np, pandas as pd

V = ['Cars', 'Motorcycles', 'Buses', 'Trucks']
calib = json.load(open('data/calibration_sources.json'))
raw = pd.read_csv('data/merged_ml_ready.csv')
tr = pd.read_csv('data/training_dataset.csv')
video = raw[raw['Source'] == 'Video_Extracted']
share = lambda g: (100 * g[V].sum() / g[V].sum().sum()).round(1)
rep = {}

# ---- A. vehicle mix (%)
mp, rows = calib['category_mapping'], {}
for loc, pct in calib['composition_pct'].items():
    if loc.startswith('_'): continue
    kept = {k: v for k, v in pct.items() if k not in mp['excluded']}; t = sum(kept.values())
    rows['Published: ' + loc] = {c: round(100 * sum(kept.get(x, 0) for x in mp[c]) / t, 1) for c in V}
vm = pd.DataFrame({r: share(g) for r, g in video.groupby('Road_Name')}).T
rows['Team video: mean of 6 junctions'] = vm.mean().round(1).to_dict()
rows['Team video: range across 6 junctions (motorcycles)'] = f"{vm['Motorcycles'].min()}-{vm['Motorcycles'].max()}"
rows['Training dataset (all 22 roads)'] = share(tr).to_dict()
raw_syn = raw[raw['Source'] == 'Synthetic_Generated']
rows['Original synthetic generator (before calibration)'] = share(raw_syn).to_dict()
rep['A_vehicle_mix_pct'] = rows

# ---- B. peak-hour factor and peak timing, weekdays, 12-hour window 8 AM-8 PM (hours 8..19), as in the survey
wd = tr[(tr['Day_Num'] < 5) & tr['Hour'].between(8, 19)]
out = []
for road, g in wd.groupby('Road_Name'):
    h = g.groupby('Hour')['Total_Vehicles'].mean()
    out.append((road, 100 * h.max() / h.sum(), int(h.loc[8:12].idxmax()), int(h.loc[15:19].idxmax())))
b = pd.DataFrame(out, columns=['road', 'phf', 'am_peak_hour', 'pm_peak_hour'])
rep['B_peak_hour'] = {'published_peak_hour_factor_pct': calib['text_findings']['peak_hour_factor_pct_of_12h_traffic'],
    'dataset_phf_pct_min_median_max': [round(b.phf.min(), 1), round(b.phf.median(), 1), round(b.phf.max(), 1)],
    'published_am_peak': calib['text_findings']['morning_peak_hour_typical'], 'published_pm_peak': calib['text_findings']['evening_peak_hour_typical'],
    'dataset_am_peak_hour_most_common': int(b.am_peak_hour.mode()[0]), 'dataset_pm_peak_hour_most_common': int(b.pm_peak_hour.mode()[0]),
    'dataset_am_peak_hour_counts': b.am_peak_hour.value_counts().to_dict(), 'dataset_pm_peak_hour_counts': b.pm_peak_hour.value_counts().to_dict()}

# ---- C. absolute volume on the one published road that matches a dataset road
ref = calib['volume_reference']['Hosur Road (NH-7) near Wipro-CSB Junction']['vehicles_12h']
g = calib['growth_reference']; growth = g['registered_2023_lakh'] / g['registered_2006_lakh']
vol = {}
for road in ['Silk Board Junction', 'Hosur Road']:
    s = wd[wd['Road_Name'] == road].groupby('Hour')['Total_Vehicles'].mean().sum()
    vol[road] = round(float(s))
rep['C_volume'] = {'published_2006_12h_vehicles_hosur_road_near_csb': ref, 'fleet_growth_2006_to_2023_x': round(growth, 2),
                   'published_scaled_by_fleet_growth': round(ref * growth), 'dataset_weekday_12h_vehicles': vol,
                   'note': 'Different year, direction coverage and survey definition; indicative only.'}

# ---- D. team video vs dataset at the four video hours
H = [8, 14, 18, 22]
vh = (video.groupby('Hour')['Total_Vehicles'].mean().reindex(H)); vidx = (vh / vh.mean()).round(2)
dh = tr[tr['Road_Name'].isin(vm.index)].groupby('Hour')['Total_Vehicles'].mean().reindex(H); didx = (dh / dh.mean()).round(2)
vw = video.groupby('Is_Weekend')['Total_Vehicles'].mean(); dw = tr[tr['Road_Name'].isin(vm.index)].groupby('Is_Weekend')['Total_Vehicles'].mean()
rep['D_video'] = {'hours': H, 'video_hourly_index': vidx.to_dict(), 'dataset_hourly_index_same_6_junctions': didx.to_dict(),
                  'video_weekend_vs_weekday_ratio': round(vw[1] / vw[0], 2), 'dataset_weekend_vs_weekday_ratio': round(dw[1] / dw[0], 2),
                  'video_rows_per_junction_hours_covered': '4 hours (8, 14, 18, 22) x 7 days; 28 of 168 cells'}
sz = lambda s: s.groupby(level=0).mean().rank(ascending=False).astype(int).to_dict()
rep['D_video']['relative_size_rank_video'] = video.groupby('Road_Name')['Total_Vehicles'].mean().rank(ascending=False).astype(int).to_dict()
rep['D_video']['relative_size_rank_dataset'] = tr[tr['Road_Name'].isin(vm.index)].groupby('Road_Name')['Total_Vehicles'].mean().rank(ascending=False).astype(int).to_dict()
rep['D_video']['relative_size_note'] = 'Matches by construction (size factors come from the video), so this is not independent validation.'

json.dump(rep, open('data/validation_report.json', 'w'), indent=2, default=str)
print(json.dumps(rep, indent=2, default=str))
