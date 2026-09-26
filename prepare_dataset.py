"""
prepare_dataset.py

Produces a clean training-ready dataset from the raw merged_ml_ready.csv.

Two documented corrections are applied here, and ONLY here — the raw file
uploaded by the team is never silently altered elsewhere in the pipeline.

1) VIDEO-EXTRACTED ROWS: an earlier version of this file had the
   Video_Extracted counts scaled up by a uniform ~197.1x factor. That scaling
   is reversed here to restore the original, genuinely observed counts.
   These rows are NOT used for model training (see train_model.py) — they
   only exist in this cleaned file for reference / future validation use.

2) SYNTHETIC ROWS: the original synthetic generator produced traffic totals
   that are nearly flat across all 24 hours (~2.4% hour-to-hour variation),
   with no rush-hour pattern. This is a known limitation of the generator,
   not real-world behavior. A documented diurnal multiplier — based on
   typical Bangalore peak/off-peak traffic patterns — is applied to give
   the data a realistic daily shape. The multiplier is normalized so each
   road's overall daily average total is UNCHANGED; traffic is only
   redistributed across hours, not inflated or deflated in total.

   This is a modeled assumption about time-of-day shape, not a measured
   fact — document it as such in the LLD / report.
"""
import pandas as pd
import numpy as np

INPUT_FILE  = "data/merged_ml_ready.csv"
OUTPUT_FILE = "data/merged_ml_ready_v2.csv"

# The scale factor identified between the original and the scaled upload
VIDEO_SCALE_FACTOR = 197.0991737635758

# Documented diurnal shape: typical Bangalore weekday traffic intensity by hour.
# 1.0 = an "average" hour. >1 = busier than average, <1 = quieter than average.
# Values are illustrative of known peak-hour behavior (AM 8-10, PM 17-20)
# and should be revisited if real hourly counts become available.
DIURNAL_MULTIPLIER_RAW = {
    0: 0.35, 1: 0.25, 2: 0.20, 3: 0.18, 4: 0.22, 5: 0.35,
    6: 0.55, 7: 0.85, 8: 1.35, 9: 1.45, 10: 1.15, 11: 0.95,
    12: 0.90, 13: 0.95, 14: 0.90, 15: 0.85, 16: 0.95, 17: 1.30,
    18: 1.55, 19: 1.45, 20: 1.10, 21: 0.80, 22: 0.55, 23: 0.40,
}

VEHICLE_COLS = ["Cars", "Motorcycles", "Buses", "Trucks"]


def restore_video_rows(df):
    mask = df["Source"] == "Video_Extracted"
    for col in VEHICLE_COLS:
        df.loc[mask, col] = (df.loc[mask, col] / VIDEO_SCALE_FACTOR).round().astype(int)
    df.loc[mask, "Total_Vehicles"] = df.loc[mask, VEHICLE_COLS].sum(axis=1)
    return df


def apply_diurnal_curve(df):
    # Normalize so the average multiplier across 24 hours is exactly 1.0 —
    # this guarantees each road's daily average total is preserved, only
    # its distribution across hours changes.
    mean_mult = np.mean(list(DIURNAL_MULTIPLIER_RAW.values()))
    diurnal = {h: v / mean_mult for h, v in DIURNAL_MULTIPLIER_RAW.items()}

    mask = df["Source"] == "Synthetic_Generated"
    mult_series = df.loc[mask, "Hour"].map(diurnal)

    for col in VEHICLE_COLS:
        df.loc[mask, col] = (df.loc[mask, col] * mult_series).round().astype(int)
    df.loc[mask, "Total_Vehicles"] = df.loc[mask, VEHICLE_COLS].sum(axis=1)
    return df


def main():
    df = pd.read_csv(INPUT_FILE)

    before_video_avg = df.loc[df["Source"] == "Video_Extracted", "Total_Vehicles"].mean()
    before_hour_std  = df.loc[df["Source"] == "Synthetic_Generated"].groupby("Hour")["Total_Vehicles"].mean().std()

    df = restore_video_rows(df)
    df = apply_diurnal_curve(df)

    after_video_avg = df.loc[df["Source"] == "Video_Extracted", "Total_Vehicles"].mean()
    after_hour_std   = df.loc[df["Source"] == "Synthetic_Generated"].groupby("Hour")["Total_Vehicles"].mean().std()

    print("=== Video-extracted rows ===")
    print(f"  Before: avg total = {before_video_avg:,.1f}  (scaled)")
    print(f"  After:  avg total = {after_video_avg:,.1f}  (restored to genuine scale)")
    print()
    print("=== Synthetic rows: hour-to-hour variation ===")
    print(f"  Before: std across hours = {before_hour_std:,.1f}  (nearly flat)")
    print(f"  After:  std across hours = {after_hour_std:,.1f}  (real diurnal shape)")

    df.to_csv(OUTPUT_FILE, index=False)
    print(f"\nSaved: {OUTPUT_FILE}  ({len(df)} rows)")


if __name__ == "__main__":
    main()
