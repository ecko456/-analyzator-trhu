#!/usr/bin/env python3
"""
Calibration of the Reversal & Confirmation indicator from its CSV log.

  python calibrate.py log.csv [more.csv ...] --out calibration.json
         [--label hit_T1] [--folds 5] [--min-samples 30] [--train-until 2026-01-01]

What it does
  1. loads one or more log.csv files (written by the ATAS indicator or by tools/Replay); only rows with
     complete == 1 are used,
  2. fits two L2-regularised logistic models (pure numpy, no scikit-learn):
       * reversal model: P(label | reversal)              rows kind == REV
       * zone model:     P(label | zone was filled)       rows kind == ZONE, filled == 1
     features = component scores s_A..s_I + context (VWAP side / slope / distance, level, sweep, ...),
  3. validates both walk-forward (expanding window: train on older rows, predict the next slice),
  4. builds a reliability table from the out-of-sample predictions: for each probability bucket the
     measured hit rate and the number of samples. The indicator shows that measured rate, and only for
     buckets with >= --min-samples samples ("probability is measured, not estimated"),
  5. derives component weights A..I from the reversal model (for the 0-100 score),
  6. writes calibration.json, which the indicator loads (Logging -> Calibration JSON).

The default label is the spec's "T1 before stop". Note: T1 (origin of the move) is further away after
bigger moves and closer when the reversal bar closes high, so T1-based models partly learn geometry.
Use --label hit_R15 for a target-neutral view (1.5 R before stop).
"""
import argparse
import json
import sys
from datetime import datetime, timezone

import numpy as np

try:
    import pandas as pd
except ImportError:  # pragma: no cover
    sys.exit("pandas is required: pip install pandas numpy")

COMPS = list("ABCDEFGHI")
S_COLS = [f"s_{c}" for c in COMPS]
DEFAULT_W = np.array([8, 15, 12, 8, 15, 7, 7, 15, 13], dtype=float)

# (column, lo, hi, fill) - values are clamped to [lo, hi]; missing values use fill
REV_FEATURES = [(c, 0, 1, 0) for c in S_COLS] + [
    ("vwap_side", -1, 1, 0),
    ("vwap_slope_atr", -2, 2, 0),
    ("vwap_dist_atr", -4, 4, 0),
    ("level_weight", 0, 1, 0),
    ("confluence", 0, 3, 0),
    ("overshoot_atr", 0, 2, 0),
    ("min_from_rth", 0, 390, 195),
]
ZONE_FEATURES = [(c, 0, 1, 0) for c in S_COLS] + [
    ("zone_score", 0, 100, 60),
    ("zone_retest", 0, 1, 0),
    ("rr_first", 0, 8, 2),
    ("vwap_side", -1, 1, 0),
    ("vwap_slope_atr", -2, 2, 0),
    ("vwap_dist_atr", -4, 4, 0),
]
SCORE_BUCKETS = [(60, 65), (65, 70), (70, 75), (75, 80), (80, 85), (85, 90), (90, 101)]
PROB_EDGES = [0.0, 0.15, 0.25, 0.35, 0.45, 0.55, 0.65, 1.01]


# ------------------------------------------------------------------ logistic regression
def fit_logistic(X, y, l2=1.0, iters=60):
    """IRLS on standardised features; returns (intercept, coef) on the raw scale."""
    mu, sd = X.mean(axis=0), X.std(axis=0)
    sd[sd < 1e-9] = 1.0
    Z = (X - mu) / sd
    n, k = Z.shape
    Zb = np.hstack([np.ones((n, 1)), Z])
    w = np.zeros(k + 1)
    reg = np.full(k + 1, l2)
    reg[0] = 0.0
    for _ in range(iters):
        p = 1 / (1 + np.exp(-np.clip(Zb @ w, -30, 30)))
        g = Zb.T @ (p - y) + reg * w
        H = (Zb * (p * (1 - p))[:, None]).T @ Zb + np.diag(reg) + 1e-9 * np.eye(k + 1)
        step = np.linalg.solve(H, g)
        w -= step
        if np.max(np.abs(step)) < 1e-9:
            break
    coef = w[1:] / sd
    intercept = w[0] - np.sum(w[1:] * mu / sd)
    return intercept, coef


def predict(model, X):
    b0, b = model
    return 1 / (1 + np.exp(-np.clip(b0 + X @ b, -30, 30)))


def auc(y, p):
    y, p = np.asarray(y), np.asarray(p)
    pos, neg = (y == 1).sum(), (y == 0).sum()
    if pos == 0 or neg == 0:
        return float("nan")
    _, inv, counts = np.unique(p, return_inverse=True, return_counts=True)
    order = np.argsort(p, kind="mergesort")
    ranks = np.empty(len(p))
    ranks[order] = np.arange(1, len(p) + 1)
    ranks = (np.bincount(inv, ranks) / counts)[inv]
    return float((ranks[y == 1].sum() - pos * (pos + 1) / 2) / (pos * neg))


def brier(y, p):
    return float(np.mean((np.asarray(p) - np.asarray(y)) ** 2))


# ------------------------------------------------------------------ data
def load(paths):
    frames = [pd.read_csv(p, low_memory=False) for p in paths]
    df = pd.concat(frames, ignore_index=True)
    df = df[pd.to_numeric(df["complete"], errors="coerce") == 1].copy()
    df["t"] = pd.to_datetime(df["time_utc"])
    return df.sort_values("t").reset_index(drop=True)


def matrix(d, feats):
    cols = []
    for c, lo, hi, fill in feats:
        v = pd.to_numeric(d[c], errors="coerce").fillna(fill).to_numpy(dtype=float) if c in d else np.full(len(d), fill)
        cols.append(np.clip(v, lo, hi))
    return np.column_stack(cols)


def label(d, name):
    return pd.to_numeric(d[name], errors="coerce").fillna(0).to_numpy(dtype=float)


def walk_forward(X, y, folds, l2, min_train=100):
    n = len(y)
    edges = np.linspace(0, n, folds + 2).astype(int)[1:]
    pred = np.full(n, np.nan)
    for i in range(folds):
        a, b = edges[i], edges[i + 1]
        if a < min_train or y[:a].min() == y[:a].max():
            continue
        pred[a:b] = predict(fit_logistic(X[:a], y[:a], l2), X[a:b])
    return pred


def reliability(pred, y):
    out = []
    for lo, hi in zip(PROB_EDGES[:-1], PROB_EDGES[1:]):
        m = (pred >= lo) & (pred < hi)
        n = int(m.sum())
        out.append({"lo": lo, "hi": hi, "n": n, "p": round(float(y[m].mean()), 4) if n else 0.0})
    return out


def rescore(d, w):
    s = matrix(d, [(c, 0, 1, 0) for c in S_COLS])
    appl = np.ones_like(s)
    appl[:, 7] = pd.to_numeric(d["h_applicable"], errors="coerce").fillna(0).to_numpy()
    appl[:, 8] = pd.to_numeric(d["i_applicable"], errors="coerce").fillna(0).to_numpy()
    return 100 * (s * appl * w).sum(axis=1) / np.maximum((appl * w).sum(axis=1), 1e-9)


def weights_from(coef):
    pos = np.clip(coef[: len(COMPS)], 0, None)
    if pos.sum() <= 0:
        return DEFAULT_W.copy()
    w = np.maximum(pos / pos.sum() * 100, 1.0)
    return w / w.sum() * 100


def score_buckets(score, y):
    out = []
    for lo, hi in SCORE_BUCKETS:
        m = (score >= lo) & (score < hi)
        n = int(m.sum())
        out.append({"lo": lo, "hi": hi, "n": n, "p": round(float(y[m].mean()), 4) if n else 0.0})
    return out


def model_json(model, feats, rel):
    b0, b = model
    return {
        "intercept": round(float(b0), 6),
        "terms": [{"col": c, "coef": round(float(k), 6), "lo": lo, "hi": hi, "fill": fill} for (c, lo, hi, fill), k in zip(feats, b)],
        "reliability": rel,
    }


def describe(name, y, pred, raw_score=None):
    m = ~np.isnan(pred)
    line = f"{name}: n={len(y)}, base rate {y.mean():.3f}"
    if m.sum() >= 30:
        line += (f" | out-of-sample n={m.sum()}: AUC {auc(y[m], pred[m]):.3f}, Brier {brier(y[m], pred[m]):.4f}"
                 f" (constant {brier(y[m], np.full(m.sum(), y[m].mean())):.4f})")
        if raw_score is not None:
            line += f", AUC of the raw 0-100 score {auc(y[m], raw_score[m]):.3f}"
    print(line)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("logs", nargs="+")
    ap.add_argument("--out", default="calibration.json")
    ap.add_argument("--label", default="hit_T1")
    ap.add_argument("--folds", type=int, default=5)
    ap.add_argument("--l2", type=float, default=10.0)
    ap.add_argument("--min-samples", type=int, default=30)
    ap.add_argument("--train-until", default=None, help="ignore rows at/after this UTC date (keep them as a hold-out)")
    ap.add_argument("--window", choices=["rth", "eth"], default=None,
                    help="keep only signals inside this trading window (log column 'window'); RTH and the European "
                         "morning differ in volatility and volume, so each gets its own calibration")
    a = ap.parse_args()

    df = load(a.logs)
    if a.train_until:
        df = df[df["t"] < pd.Timestamp(a.train_until)]
    if a.window:
        if "window" not in df.columns:
            sys.exit("log has no 'window' column - re-run the replay with the current version")
        df = df[df["window"].astype(str).str.lower() == a.window]
    rev = df[df["kind"] == "REV"].reset_index(drop=True)
    zone = df[(df["kind"] == "ZONE") & (pd.to_numeric(df["filled"], errors="coerce") == 1)].reset_index(drop=True)
    print(f"period {df['t'].min()} .. {df['t'].max()} | REV {len(rev)} | filled zones {len(zone)} | label {a.label}")
    if len(rev) < 200:
        sys.exit("not enough REV rows (need >= 200)")

    # ---- reversal model
    Xr, yr = matrix(rev, REV_FEATURES), label(rev, a.label)
    pr = walk_forward(Xr, yr, a.folds, a.l2)
    describe("REV ", yr, pr, pd.to_numeric(rev["score"]).to_numpy())
    rev_model = fit_logistic(Xr, yr, a.l2)
    rev_rel = reliability(pr, yr)
    print("  terms: " + ", ".join(f"{c}={k:+.3f}" for (c, *_), k in zip(REV_FEATURES, rev_model[1])))

    # component weights from the score-only part of the model, validated out of sample
    Xs = matrix(rev, [(c, 0, 1, 0) for c in S_COLS])
    new_w = weights_from(fit_logistic(Xs, yr, a.l2)[1])
    n = len(rev)
    edges = np.linspace(0, n, a.folds + 2).astype(int)[1:]
    oos = np.full(n, np.nan)
    for i in range(a.folds):
        lo, hi = edges[i], edges[i + 1]
        if lo < 100:
            continue
        wf = weights_from(fit_logistic(Xs[:lo], yr[:lo], a.l2)[1])
        oos[lo:hi] = rescore(rev.iloc[lo:hi], wf)
    m = ~np.isnan(oos)
    print(f"  score AUC out-of-sample: spec weights {auc(yr[m], pd.to_numeric(rev['score']).to_numpy()[m]):.3f}"
          f" -> calibrated weights {auc(yr[m], oos[m]):.3f}")
    print("  weights: " + " ".join(f"{c}={v:.1f}" for c, v in zip(COMPS, new_w)))

    # ---- zone model
    zone_json, zone_buckets = None, []
    if len(zone) >= 60:
        Xz, yz = matrix(zone, ZONE_FEATURES), label(zone, a.label)
        pz = walk_forward(Xz, yz, a.folds, a.l2 * 3, min_train=40)
        describe("ZONE", yz, pz, pd.to_numeric(zone["zone_score"], errors="coerce").fillna(60).to_numpy())
        zone_model = fit_logistic(Xz, yz, a.l2 * 3)
        zone_json = model_json(zone_model, ZONE_FEATURES, reliability(pz, yz))
        zone_buckets = score_buckets(pd.to_numeric(zone["zone_score"], errors="coerce").fillna(0).to_numpy(), yz)
        r = pd.to_numeric(zone["result_r"], errors="coerce")
        print(f"  filled zones: avg R {r.mean():+.3f}, median {r.median():+.2f}, win (R>0) {(r > 0).mean():.3f}")
    else:
        print(f"ZONE: {len(zone)} filled zones - need >= 60 for a model")

    out = {
        "version": 2,
        "created": datetime.now(timezone.utc).isoformat(timespec="seconds"),
        "label": a.label,
        "window": a.window or "",
        "source": [str(x) for x in a.logs],
        "period": [str(df["t"].min()), str(df["t"].max())],
        "min_samples": a.min_samples,
        "weights": {c: round(float(v), 2) for c, v in zip(COMPS, new_w)},
        "reversal": score_buckets(oos, yr),
        "zone": zone_buckets,
        "reversal_model": model_json(rev_model, REV_FEATURES, rev_rel),
    }
    if zone_json:
        out["zone_model"] = zone_json
    with open(a.out, "w", encoding="utf-8") as f:
        json.dump(out, f, indent=2)
    print("written", a.out)
    for name, rel in (("reversal", rev_rel), ("zone", zone_json["reliability"] if zone_json else [])):
        for b in rel:
            shown = "" if b["n"] >= a.min_samples else "   (hidden, < min samples)"
            print(f"  {name:8s} model p {b['lo']:.2f}-{b['hi']:.2f}: n={b['n']:5d} measured {b['p']:.3f}{shown}")


if __name__ == "__main__":
    main()
