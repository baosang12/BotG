#!/usr/bin/env python3
"""
Postrun analysis: slippage and latency percentiles, hourly fill-rate, top slippages, and plots.

Inputs precedence:
  1) --fills path: reconstructed closed trades (preferred)
  2) --orders path: raw orders events (REQUEST/ACK/FILL)
  3) --logdir: auto-discover latest telemetry_run_* under <logdir>/artifacts and use its files

Outputs (written to --outdir, default: path_issues):
  - slip_latency_percentiles.json
  - fillrate_hourly.csv
  - top_slippage.csv
  - slippage_hist.png (if matplotlib available)
  - latency_percentiles.png (if matplotlib available)
  - fillrate_by_hour.png (if matplotlib available)

The script is resilient to missing columns: it infers common column names and skips metrics it cannot compute.
"""
from __future__ import annotations

import argparse
import json
import math
import os
from pathlib import Path
from typing import Dict, Optional, Tuple

import pandas as pd

try:
    import numpy as np
except Exception:  # pragma: no cover
    np = None  # type: ignore

try:  # plots optional
    import matplotlib
    matplotlib.use("Agg")
    import matplotlib.pyplot as plt
except Exception:  # pragma: no cover
    plt = None  # type: ignore


def find_col(cols, candidates):
    """Return the first column present in cols among candidates (case-insensitive)."""
    m = {c.lower(): c for c in cols}
    for cand in candidates:
        if cand.lower() in m:
            return m[cand.lower()]
    return None


def latest_run_dir(logdir: Path) -> Optional[Path]:
    art = logdir / "artifacts"
    if not art.exists():
        return None
    runs = [p for p in art.iterdir() if p.is_dir() and p.name.startswith("telemetry_run_")]
    if not runs:
        return None
    runs.sort(key=lambda p: p.stat().st_mtime, reverse=True)
    return runs[0]


def load_fills(path: Path) -> pd.DataFrame:
    df = pd.read_csv(path)
    return df


def derive_from_orders(orders_csv: Path) -> Tuple[pd.DataFrame, pd.DataFrame]:
    """Build fills dataframe and requests/fills per-hour counts from raw orders events.

    Returns (fills_df, hourly_counts_df)
    """
    odf = pd.read_csv(orders_csv)
    cols = list(odf.columns)
    # Identify key fields (some may be missing in simplified CSVs)
    ev_col = find_col(cols, ["event", "type", "status", "action"])
    ts_col = find_col(cols, ["timestamp", "time", "event_time"]) or cols[0]
    oid_col = find_col(cols, ["orderid", "order_id", "id"]) or "order_id"
    req_price = find_col(cols, ["price_requested", "requested_price", "request_price", "price_request"])  # optional
    fill_price = find_col(cols, ["price_filled", "filled_price", "execution_price"])  # optional
    size_col = find_col(cols, ["size", "quantity", "qty", "filled_size", "size_filled"])  # optional

    # Normalize
    odf["_ts"] = pd.to_datetime(odf[ts_col], errors="coerce", utc=True)
    odf = odf.dropna(subset=["_ts"]).sort_values([oid_col, "_ts"]).reset_index(drop=True)

    # Per-hour counts
    hdf = odf.copy()
    hdf["hour"] = hdf["_ts"].dt.floor("H")
    if ev_col and ev_col in hdf.columns:
        rq = hdf[hdf[ev_col].astype(str).str.upper().eq("REQUEST")].groupby("hour").size().rename("requests")
        fl = hdf[hdf[ev_col].astype(str).str.upper().eq("FILL")].groupby("hour").size().rename("fills")
        hourly = pd.concat([rq, fl], axis=1).fillna(0)
        hourly["fill_rate"] = hourly.apply(lambda r: (r["fills"] / r["requests"]) if r.get("requests", 0) > 0 else math.nan, axis=1)
    else:
        # Assume all rows are fills; requests unknown
        fl = hdf.groupby("hour").size().rename("fills")
        hourly = pd.concat([fl], axis=1)
        hourly["requests"] = math.nan
        hourly["fill_rate"] = math.nan
    hourly = hourly.reset_index().rename(columns={"hour": "timestamp"})

    # Derive per-order REQUEST and FILL timestamps for latency
    if ev_col and ev_col in odf.columns:
        req_time = (
            odf[odf[ev_col].astype(str).str.upper().eq("REQUEST")]  # type: ignore[index]
            .groupby(oid_col)["_ts"].min()
            .rename("request_ts")
        )
        fill_time = (
            odf[odf[ev_col].astype(str).str.upper().eq("FILL")]
            .groupby(oid_col)["_ts"].max()
            .rename("fill_ts")
        )
        base = pd.concat([req_time, fill_time], axis=1).dropna()
        if not base.empty:
            base["latency_ms"] = (base["fill_ts"] - base["request_ts"]).dt.total_seconds() * 1000.0
        else:
            base = pd.DataFrame(columns=["request_ts", "fill_ts", "latency_ms"])  # empty
    else:
        # Without event classification, we can't compute latency; treat each row as a fill-only observation
        base = odf[[oid_col, "_ts"]].copy().rename(columns={"_ts": "fill_ts"})
        base["request_ts"] = pd.NaT
        base["latency_ms"] = pd.NA
        base = base.set_index(oid_col)

    # Slippage if prices available and we have REQUEST vs FILL separation
    if ev_col and req_price and fill_price and req_price in odf.columns and fill_price in odf.columns:
        last_fill = odf[odf[ev_col].astype(str).str.upper().eq("FILL")].sort_values([oid_col, "_ts"]).groupby(oid_col).tail(1)
        last_req = odf[odf[ev_col].astype(str).str.upper().eq("REQUEST")].sort_values([oid_col, "_ts"]).groupby(oid_col).head(1)
        price_df = pd.merge(
            last_req[[oid_col, req_price]], last_fill[[oid_col, fill_price, "_ts"]], on=oid_col, how="inner"
        )
        price_df = price_df.rename(columns={"_ts": "fill_ts"})
        try:
            price_df["slippage"] = price_df[fill_price].astype(float) - price_df[req_price].astype(float)
        except Exception:
            price_df["slippage"] = pd.NA
        base = pd.merge(base.reset_index(), price_df[[oid_col, "slippage", "fill_ts"]], on=[oid_col, "fill_ts"], how="left").set_index(oid_col)
    else:
        base = base.reset_index(); base["slippage"] = pd.NA; base = base.set_index(oid_col)

    # Attach size if exists (from last fill)
    if size_col and size_col in odf.columns and (not ev_col or ev_col not in odf.columns):
        # No event classification: take per-order last size
        sf = odf.sort_values([oid_col, "_ts"]).groupby(oid_col).tail(1)[[oid_col, size_col]].rename(columns={size_col: "size"})
        base = pd.merge(base.reset_index(), sf, on=oid_col, how="left").set_index(oid_col)
    elif size_col and size_col in odf.columns:
        sf = (
            odf[odf[ev_col].astype(str).str.upper().eq("FILL")]
            .sort_values([oid_col, "_ts"]).groupby(oid_col).tail(1)[[oid_col, size_col]]
            .rename(columns={size_col: "size"})
        )
        base = pd.merge(base.reset_index(), sf, on=oid_col, how="left").set_index(oid_col)

    # Fills-like dataframe
    fdf = base.reset_index().rename(columns={"index": oid_col})
    fdf["timestamp"] = fdf["fill_ts"]
    return fdf, hourly


def percentiles(series: pd.Series, qs=(0.5, 0.75, 0.9, 0.95, 0.99)) -> Dict[str, float]:
    s = pd.to_numeric(series, errors="coerce").dropna()
    if s.empty:
        return {f"p{int(q*100)}": math.nan for q in qs}
    if np is not None:
        vals = np.quantile(s.values, qs)
    else:
        vals = s.quantile(list(qs)).values
    return {f"p{int(q*100)}": float(v) for q, v in zip(qs, vals)}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--fills", help="Path to closed_trades_fifo_reconstructed.csv")
    ap.add_argument("--orders", help="Path to orders.csv")
    ap.add_argument("--logdir", help="Log root to auto-discover latest run")
    ap.add_argument("--outdir", default="path_issues", help="Output directory")
    args = ap.parse_args()

    out = Path(args.outdir)
    out.mkdir(parents=True, exist_ok=True)

    fills_path: Optional[Path] = Path(args.fills) if args.fills else None
    orders_path: Optional[Path] = Path(args.orders) if args.orders else None

    if not fills_path and not orders_path and args.logdir:
        lrd = latest_run_dir(Path(args.logdir))
        if lrd:
            if (lrd / "closed_trades_fifo_reconstructed.csv").exists():
                fills_path = lrd / "closed_trades_fifo_reconstructed.csv"
            elif (lrd / "closed_trades_fifo.csv").exists():
                fills_path = lrd / "closed_trades_fifo.csv"
            if (lrd / "orders.csv").exists():
                orders_path = lrd / "orders.csv"

    fills_df: Optional[pd.DataFrame] = None
    hourly: Optional[pd.DataFrame] = None

    if fills_path and fills_path.exists():
        fills_df = load_fills(fills_path)
    elif orders_path and orders_path.exists():
        fills_df, hourly = derive_from_orders(orders_path)
    else:
        raise SystemExit("No input found: provide --fills or --orders or --logdir with artifacts.")

    # Column inference for fills
    cols = list(fills_df.columns)
    ts_col = find_col(cols, ["timestamp", "time", "close_time", "closed_time", "fill_ts", "_ts"]) or cols[0]
    lat_col = find_col(cols, ["latency_ms", "latency", "latencyMillis"])  # optional
    slip_col = find_col(cols, ["slippage", "slip", "raw_slip", "price_slip"])  # optional
    # If missing slippage, try to compute from prices if available
    if slip_col is None:
        pr = find_col(cols, ["price_filled", "filled_price", "execution_price"])  # price filled
        prq = find_col(cols, ["price_requested", "requested_price", "request_price"])  # price requested
        if pr and prq and pr in fills_df.columns and prq in fills_df.columns:
            try:
                fills_df["__slippage"] = fills_df[pr].astype(float) - fills_df[prq].astype(float)
                slip_col = "__slippage"
            except Exception:
                pass

    # Normalize timestamp
    f = fills_df.copy()
    f["_ts"] = pd.to_datetime(f[ts_col], errors="coerce", utc=True)
    f = f.dropna(subset=["_ts"]).sort_values("_ts")

    # Build hourly if not from orders
    if hourly is None:
        # Without explicit requests count, report fills and unknown requests
        h = f.copy(); h["timestamp"] = h["_ts"].dt.floor("H")
        hourly = h.groupby("timestamp").size().reset_index(name="fills")
        hourly["requests"] = math.nan
        hourly["fill_rate"] = math.nan

    # Compute percentiles
    out_json = {}
    if slip_col and slip_col in f.columns:
        out_json["slippage_abs"] = percentiles(f[slip_col].abs())
    else:
        out_json["slippage_abs"] = {k: math.nan for k in ["p50", "p75", "p90", "p95", "p99"]}
    if lat_col and lat_col in f.columns:
        out_json["latency_ms"] = percentiles(f[lat_col])
    else:
        out_json["latency_ms"] = {k: math.nan for k in ["p50", "p75", "p90", "p95", "p99"]}

    with open(out / "slip_latency_percentiles.json", "w", encoding="utf-8") as fp:
        json.dump(out_json, fp, ensure_ascii=False, indent=2)

    # Hourly fill rate & medians
    if lat_col and lat_col in f.columns:
        med_lat = f.assign(hour=f["_ts"].dt.floor("H")).groupby("hour")[lat_col].median().rename("median_latency_ms")
    else:
        med_lat = pd.Series(dtype=float)
    if slip_col and slip_col in f.columns:
        med_slp = f.assign(hour=f["_ts"].dt.floor("H")).groupby("hour")[slip_col].median().rename("median_slippage")
    else:
        med_slp = pd.Series(dtype=float)
    hourly = hourly.set_index("timestamp").join(med_lat, how="left").join(med_slp, how="left").reset_index()
    hourly.to_csv(out / "fillrate_hourly.csv", index=False)

    # Top slippages
    top = pd.DataFrame(columns=["timestamp", "slippage"])  # default
    if slip_col and slip_col in f.columns:
        top = f[["_ts", slip_col]].copy().rename(columns={"_ts": "timestamp", slip_col: "slippage"})
        top = top.reindex(columns=[c for c in ["timestamp", "slippage"] if c in top.columns])
        top["abs_slip"] = top["slippage"].abs()
        top = top.sort_values("abs_slip", ascending=False).head(20)
    top.to_csv(out / "top_slippage.csv", index=False)

    # Plots
    if plt is not None:
        try:
            if slip_col and slip_col in f.columns:
                plt.figure(figsize=(6, 4))
                f[slip_col].dropna().hist(bins=50)
                plt.title("Slippage histogram")
                plt.tight_layout()
                plt.savefig(out / "slippage_hist.png", dpi=120)
                plt.close()
            if lat_col and lat_col in f.columns:
                percs = [50, 75, 90, 95, 99]
                vals = [out_json["latency_ms"].get(f"p{p}") for p in percs]
                plt.figure(figsize=(6, 4))
                plt.plot(percs, vals, marker="o")
                plt.xlabel("percentile")
                plt.ylabel("latency (ms)")
                plt.title("Latency percentiles")
                plt.grid(True, alpha=0.3)
                plt.tight_layout()
                plt.savefig(out / "latency_percentiles.png", dpi=120)
                plt.close()
            if not hourly.empty:
                plt.figure(figsize=(7, 4))
                ax = plt.gca()
                if "fill_rate" in hourly.columns:
                    ax.plot(hourly["timestamp"], hourly["fill_rate"], label="fill_rate")
                if "median_latency_ms" in hourly.columns:
                    ax2 = ax.twinx()
                    ax2.plot(hourly["timestamp"], hourly["median_latency_ms"], color="orange", label="median_latency")
                ax.set_title("Fill rate by hour")
                plt.tight_layout()
                plt.savefig(out / "fillrate_by_hour.png", dpi=120)
                plt.close()
        except Exception:
            # Write marker file if plots fail
            with open(out / "plots_skipped.txt", "w", encoding="utf-8") as fp:
                fp.write("Plots skipped due to matplotlib/runtime error.\n")

    # Minimal human summary
    with open(out / "postrun_summary.txt", "w", encoding="utf-8") as fp:
        fp.write("Slippage/Latency percentiles (see slip_latency_percentiles.json) and hourly fill-rate saved.\n")


if __name__ == "__main__":
    main()
