# Agent A - Telemetry Analyzer (MVP)

**Offline telemetry analysis tool** - generates PDF reports and KPI metrics from trading logs.

## Features

✅ **Equity Analysis**
- Equity curve plotting (equity vs balance)
- Total P&L calculation and percentage
- Maximum drawdown detection
- Peak equity tracking

✅ **P&L Breakdown** (Agent A)
- Open P&L tracking (unrealized)
- Closed P&L accumulation (realized)
- Monotonic validation
- Thread safety verification

✅ **Trade Statistics**
- Order count and fill rate
- Slippage analysis (avg, max)
- Trade distribution

✅ **Risk Metrics**
- R-usage violations detection
- Margin usage tracking
- Risk exposure monitoring

✅ **Outputs**
- `report.pdf` - Multi-page visual report
- `kpi.json` - Structured KPI metrics

## Installation

```bash
pip install -r scripts/requirements.txt
```

**Requirements:**
- Python 3.7+
- pandas >= 2.0.0
- matplotlib >= 3.7.0

## Usage

### Basic Command

```bash
python scripts/postrun_report.py \
  --orders <path/to/orders.csv> \
  --risk <path/to/risk_snapshots.csv> \
  --out <output_directory>
```

### Examples

**Local files:**
```bash
python scripts/postrun_report.py \
  --orders orders.csv \
  --risk risk_snapshots.csv \
  --out reports/
```

**Full paths (Windows):**
```bash
python scripts/postrun_report.py \
  --orders "D:/botg/logs/orders.csv" \
  --risk "D:/botg/logs/risk_snapshots.csv" \
  --out "D:/botg/reports/"
```

**From artifacts:**
```bash
python scripts/postrun_report.py \
  --orders artifacts/gate2_12345/orders.csv \
  --risk artifacts/gate2_12345/risk_snapshots.csv \
  --out artifacts/gate2_12345/analysis/
```

### Sample Data Test

```bash
python scripts/postrun_report.py \
  --orders scripts/sample_data/orders_sample.csv \
  --risk scripts/sample_data/risk_snapshots_sample.csv \
  --out scripts/sample_data/output
```

## Input Files

### `orders.csv`
Required columns:
- `order_id` - Unique order identifier
- `timestamp` - Order timestamp (ISO 8601)
- `symbol` - Trading pair (e.g., EURUSD)
- `direction` - Buy/Sell
- `volume` - Order size
- `status` - Order status (Filled/Pending/Cancelled)

Optional columns:
- `requested_price` - Requested fill price
- `fill_price` - Actual fill price
- `pnl` - Profit/Loss for closed positions

### `risk_snapshots.csv`
Required columns:
- `timestamp_utc` - Snapshot timestamp (ISO 8601)
- `equity` - Current account equity
- `balance` - Account balance

**Agent A columns** (highly recommended):
- `open_pnl` - Unrealized P&L (equity - balance)
- `closed_pnl` - Cumulative realized P&L

Optional columns:
- `margin` - Used margin
- `free_margin` - Available margin
- `drawdown` - Current drawdown
- `R_used` - Risk units used
- `exposure` - Market exposure

## Output Files

### `report.pdf`
Multi-page PDF report containing:

**Page 1: Overview**
- Equity Curve (equity vs balance over time)
- P&L Breakdown (open vs closed P&L) - Agent A
- Drawdown Chart
- KPI Summary Table

### `kpi.json`
Structured JSON with metrics:

```json
{
  "metadata": {
    "generated_at": "2025-10-10T02:06:46Z",
    "orders_file": "orders_sample.csv",
    "risk_file": "risk_snapshots_sample.csv",
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
    "avg_slippage": 0.0003,
    "max_slippage": 0.020
  },
  "risk": {
    "has_open_pnl_column": true,
    "has_closed_pnl_column": true,
    "final_closed_pnl": 314.24,
    "closed_pnl_monotonic": true,
    "final_open_pnl": 40.0,
    "r_violations_count": 0,
    "max_r_used": 0.12,
    "max_margin_usage_pct": 2.5
  }
}
```

## Agent A Validation

The analyzer validates **Agent A** implementation (PR #238):

✅ **Schema Check**: Detects `open_pnl` and `closed_pnl` columns
✅ **Formula Check**: Verifies `open_pnl = equity - balance`
✅ **Accumulation**: Checks `closed_pnl` is monotonically increasing
✅ **Thread Safety**: Validates cumulative totals

## Sample Output

See `scripts/sample_data/output/` for example outputs:
- `report.pdf` - Sample PDF report (18 snapshots, 32 orders)
- `kpi.json` - Sample KPI metrics

## Integration with Gate2 Workflow

### Post-Run Analysis

After a Gate2 run completes:

```powershell
# 1. Download artifacts
$rid = 18393525101
$DST = "D:\botg\logs\artifacts\gate2_$rid"
gh run download $rid --dir $DST

# 2. Run analysis
python scripts/postrun_report.py `
  --orders "$DST/artifacts_*/orders.csv" `
  --risk "$DST/artifacts_*/risk_snapshots.csv" `
  --out "$DST/analysis/"

# 3. View results
Start-Process "$DST/analysis/report.pdf"
```

### Automated Pipeline

Add to `.github/workflows/gate24h.yml`:

```yaml
- name: Generate Telemetry Report
  if: always()
  run: |
    python scripts/postrun_report.py \
      --orders ${{ env.RUN_DIR }}/orders.csv \
      --risk ${{ env.RUN_DIR }}/risk_snapshots.csv \
      --out ${{ env.RUN_DIR }}/analysis/
    
- name: Upload Analysis
  uses: actions/upload-artifact@v4
  with:
    name: telemetry-analysis
    path: ${{ env.RUN_DIR }}/analysis/
```

## Development

### Add New Metrics

Edit `TelemetryAnalyzer` class in `postrun_report.py`:

1. Add analysis method (e.g., `analyze_winrate()`)
2. Call from `compute_kpi()`
3. Add to KPI output
4. Optional: Add plot to PDF

### Add New Charts

Edit `generate_pdf_report()`:

1. Create new matplotlib subplot
2. Implement plot function
3. Add to PDF pages

## Troubleshooting

**Missing packages:**
```bash
pip install pandas matplotlib
```

**File not found:**
- Check file paths are absolute or relative to script location
- On Windows, use forward slashes or escaped backslashes

**Empty plots:**
- Verify CSV has data
- Check timestamp column format (ISO 8601 preferred)

**PDF won't open:**
- Check disk space
- Verify write permissions on output directory

## Roadmap

Future enhancements:
- [ ] Win rate calculation (requires closed_trades.csv)
- [ ] Sharpe ratio & Sortino ratio
- [ ] Monthly breakdown charts
- [ ] Trade distribution histogram
- [ ] Risk-adjusted returns
- [ ] Comparison mode (multiple runs)

## License

Part of BotG trading system - Agent A implementation.

## Credits

**Agent A** - Risk ledger synchronization (PR #238)
- open_pnl calculation (equity - balance)
- closed_pnl thread-safe accumulation
- Fail-fast exceptions
