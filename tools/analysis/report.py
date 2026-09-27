#!/usr/bin/env python3
"""
Back-test report from Replay/indicator logs (markdown to stdout).

  python report.py RUN_DIR [RUN_DIR ...] [--split 2025-10-01] [--names "a|b|..."]

Periods: "train" = before --split, "test" = from --split on (the calibration never saw it).
Zone results: R(T1) = exit at T1 or stop (mark-to-market after 36 bars); R(1.5R) = fixed 1.5 R target.
"""
import argparse
import os

import numpy as np
import pandas as pd


def load(run):
    d = pd.read_csv(os.path.join(run, "log.csv"), low_memory=False)
    d = d[pd.to_numeric(d["complete"], errors="coerce") == 1].copy()
    d["t"] = pd.to_datetime(d["time_utc"])
    for c in ["result_r", "result_r15", "hit_T1", "hit_R15", "stop_hit", "score", "rth", "filled", "with_trend", "p_model", "vwap_slope_atr"]:
        if c in d:
            d[c] = pd.to_numeric(d[c], errors="coerce")
    return d


def stats(x, col):
    v = x[col].dropna()
    n = len(v)
    if n == 0:
        return "–"
    se = v.std(ddof=1) / np.sqrt(n) if n > 1 else float("nan")
    return f"{v.mean():+.2f} ± {se:.2f}"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("runs", nargs="+")
    ap.add_argument("--split", default="2025-10-01")
    ap.add_argument("--names", default=None)
    a = ap.parse_args()
    names = a.names.split("|") if a.names else [os.path.basename(r.rstrip("/")) for r in a.runs]

    print("| varianta | období | session | reversaly / session | potvrzení / session | vyplněné zóny | T1 zasažen | R (výstup T1) | R (výstup 1,5R) | win 1,5R |")
    print("|---|---|---:|---:|---:|---:|---:|---:|---:|---:|")
    for run, name in zip(a.runs, names):
        d = load(run)
        d["per"] = np.where(d["t"] < pd.Timestamp(a.split), "trénink", "test")
        for per in ["trénink", "test"]:
            x = d[d["per"] == per]
            ns = x["session"].nunique()
            rev = x[x["kind"] == "REV"]
            conf = x[x["kind"] == "CONF"]
            z = x[(x["kind"] == "ZONE") & (x["filled"] == 1)]
            win = (z["result_r15"] > 0).mean() if len(z) else float("nan")
            print(f"| {name} | {per} | {ns} | {len(rev) / max(ns, 1):.1f} | {len(conf) / max(ns, 1):.2f} | {len(z)} | "
                  f"{z['hit_T1'].mean():.0%} | {stats(z, 'result_r')} | {stats(z, 'result_r15')} | {win:.0%} |")
    print()

    # detail for the first run: trend split and zone types
    d = load(a.runs[0])
    d["per"] = np.where(d["t"] < pd.Timestamp(a.split), "trénink", "test")
    z = d[(d["kind"] == "ZONE") & (d["filled"] == 1)].copy()
    print(f"Detail `{names[0]}` – vyplněné zóny podle směru VWAP trendu a typu:")
    print()
    print("| období | trend | typ zóny | n | T1 zasažen | R (výstup T1) | R (výstup 1,5R) |")
    print("|---|---|---|---:|---:|---:|---:|")
    for per in ["trénink", "test"]:
        for tr, lab in [(1, "po trendu"), (0, "proti trendu")]:
            for zt in ["conf_vpoc", "retest", None]:
                x = z[(z["per"] == per) & (z["with_trend"] == tr)]
                if zt:
                    x = x[x["zone_type"] == zt]
                if len(x) == 0:
                    continue
                print(f"| {per} | {lab} | {zt or 'vše'} | {len(x)} | {x['hit_T1'].mean():.0%} | {stats(x, 'result_r')} | {stats(x, 'result_r15')} |")
    print()

    # reliability of the displayed probability (calibrated run, test period)
    for run, name in zip(a.runs, names):
        d = load(run)
        if "p_model" not in d or d["p_model"].notna().sum() == 0:
            continue
        x = d[(d["kind"] == "REV") & (d["t"] >= pd.Timestamp(a.split)) & d["p_model"].notna()].copy()
        x["b"] = pd.cut(x["p_model"], [0, 0.2, 0.3, 0.4, 0.5, 1.0])
        print(f"Spolehlivost zobrazené pravděpodobnosti `{name}` (reversal → T1 před stopem, jen testovací období):")
        print()
        print("| zobrazeno | n | průměr zobrazené P | naměřeno |")
        print("|---|---:|---:|---:|")
        for b, g in x.groupby("b", observed=True):
            print(f"| {b} | {len(g)} | {g['p_model'].mean():.0%} | {g['hit_T1'].mean():.0%} |")
        x["sb"] = pd.cut(x["score"], [60, 70, 80, 90, 101], right=False)
        print()
        print(f"Kalibrované skóre vs. úspěšnost `{name}` (test):")
        print()
        print("| skóre | n | T1 zasažen |")
        print("|---|---:|---:|")
        for b, g in x.groupby("sb", observed=True):
            print(f"| {b} | {len(g)} | {g['hit_T1'].mean():.0%} |")
        print()


if __name__ == "__main__":
    main()
