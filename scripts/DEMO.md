# Quick Demo - Agent A Telemetry Analyzer

## Run the Demo

```bash
python scripts/postrun_report.py \
  --orders scripts/sample_data/orders_sample.csv \
  --risk scripts/sample_data/risk_snapshots_sample.csv \
  --out scripts/sample_data/output
```

## Expected Output

```
======================================================================
  AGENT A - TELEMETRY ANALYZER (MVP)
======================================================================

Loading orders from: scripts\sample_data\orders_sample.csv
  → 33 orders loaded
Loading risk snapshots from: scripts\sample_data\risk_snapshots_sample.csv
  → 18 snapshots loaded

Computing KPI...
  ✓ KPI computed

Generating PDF report: scripts\sample_data\output\report.pdf
  ✓ Page 1: Overview (equity, P&L, drawdown, KPI)
  → PDF saved: scripts\sample_data\output\report.pdf

Saving KPI JSON: scripts\sample_data\output\kpi.json
  → JSON saved: scripts\sample_data\output\kpi.json

======================================================================
  ✅ ANALYSIS COMPLETE
======================================================================

Outputs:
  - scripts\sample_data\output\report.pdf
  - scripts\sample_data\output\kpi.json
```

## Sample KPI Results

```json
{
  "metadata": {
    "generated_at": "2025-10-10T02:06:46Z",
    "orders_count": 33,
    "snapshots_count": 18
  },
  "equity": {
    "initial_equity": 10000.0,
    "final_equity": 10040.0,
    "total_pnl": 40.0,
    "total_pnl_pct": 0.4,
    "max_drawdown": -30.0,
    "max_drawdown_pct": -0.30
  },
  "trades": {
    "total_orders": 32,
    "fill_rate_pct": 96.97,
    "avg_slippage": 0.0003
  },
  "risk": {
    "has_open_pnl_column": true,
    "has_closed_pnl_column": true,
    "final_closed_pnl": 314.24,
    "closed_pnl_monotonic": true,
    "final_open_pnl": 40.0,
    "r_violations_count": 0,
    "max_r_used": 0.12
  }
}
```

## PDF Report Contents

**Page 1: Overview**
- Top Left: Equity Curve (equity vs balance over time)
- Top Right: P&L Breakdown (open vs closed P&L) - Agent A
- Bottom Left: Drawdown Chart
- Bottom Right: KPI Summary Table

## Files Included

- `scripts/postrun_report.py` (17 KB) - Main analyzer script
- `scripts/requirements.txt` - Python dependencies
- `scripts/README_TELEMETRY_ANALYZER.md` - Full documentation
- `scripts/sample_data/orders_sample.csv` (2 KB) - Sample orders
- `scripts/sample_data/risk_snapshots_sample.csv` (1 KB) - Sample risk data
- `scripts/sample_data/output/report.pdf` (36 KB) - Generated PDF
- `scripts/sample_data/output/kpi.json` (1 KB) - Generated KPI

## Next Steps

1. **View PDF**: Open `scripts/sample_data/output/report.pdf`
2. **Inspect KPI**: Check `scripts/sample_data/output/kpi.json`
3. **Use with real data**: Replace sample CSVs with your telemetry files
4. **Integrate**: Add to Gate2 workflow (see README)

## Agent A Validation

✅ Detects `open_pnl` and `closed_pnl` columns
✅ Verifies monotonic accumulation
✅ Tracks final closed P&L: 314.24
✅ Zero R-violations detected
