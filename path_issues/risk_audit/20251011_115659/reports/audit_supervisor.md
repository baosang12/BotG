# Gate2 Risk Audit Report

**Audit Date**: 2025-10-11 12:06:45 UTC
**Artifacts Source**: C:\Users\TechCare\AppData\Local\Temp\botg_artifacts\gate2_18393525101\artifacts_20251010_081328

---

## What Changed (3 Key Findings)

### 1. R4 Missing Columns

**Status**: ❌ NG

**Evidence**:
- orders.csv missing column: timestamp_created_utc
- orders.csv missing column: timestamp_filled_utc
- risk_snapshots.csv missing column: margin_used
- telemetry.csv missing column: timestamp_utc
- telemetry.csv missing column: balance
- telemetry.csv missing column: open_pnl
- telemetry.csv missing column: equity
- closed_trades_fifo.csv missing column: close_time_utc
- closed_trades_fifo.csv missing column: open_time_utc
- closed_trades_fifo.csv missing column: pnl
- closed_trades_fifo.csv missing column: symbol

**Quantitative Impact**:

### 2. R6 Friction Anomaly

**Status**: ❌ NG

**Evidence**:
- Constant slippage detected (std < 1e-6)

**Quantitative Impact**:
- Fill Rate: 33.33%

### 3. R7 Order Explosion

**Status**: ❌ NG

**Evidence**:
- Order explosion: 357,194 fills > 300k threshold

**Quantitative Impact**:
- Total Fills: 357,194

---

## So What (3 Impacts)

1. **Unrealistic Performance**: 100% fill rate and constant slippage indicate paper mode lacks market friction, inflating backtest results and reducing production readiness confidence.

---

## What Next (3 Immediate Actions + 1 A/B Change)

1. **Add Market Friction to Harness**: Modify `Harness/PaperTradingEngine.cs` to add ±0.1% randomized slippage and 5% order rejection rate (lines ~120-150, in FillOrder method).

---

## Single A/B Change for Next Gate2 Run

**🎯 RECOMMENDED A/B TEST**: Add market friction to Harness paper mode

**Change**: Modify `Harness/PaperTradingEngine.cs` FillOrder() method:
```csharp
// Add after line ~125 (before setting fill price)
var slippage = Random.Shared.NextDouble() * 0.002 - 0.001; // ±0.1%
var rejectChance = Random.Shared.NextDouble();
if (rejectChance < 0.05) { order.Status = OrderStatus.Rejected; return; }
fillPrice = requestedPrice * (1 + slippage);
```

**Expected Impact**:
- Fill rate: 33.3% → ~95%
- Slippage std: ~0 → ~0.0006 (realistic variance)
- P&L reduction: ~2-5% (more conservative/realistic)

**Validation**: Compare `friction_stats.csv` before/after to confirm `constant_slippage_flag=OK` and `fill_100pct_flag=OK`.

---

## Summary Statistics

- **Total Fills**: 357,194
- **Fill Rate**: 33.33%
- **Latency**: p50=8.0ms, p95=13.0ms, p99=14.0ms
- **Span**: 24.00h (risk snapshots)
- **Risk Violations**: 0 at -3R, 0 at -6R
- **Gaps Detected**: 0

---

## Risk Assessment Matrix

| Risk ID | Category | Status | Severity |
|---------|----------|--------|----------|
| R1_schema_validation | R1 Schema Validation | ✅ OK | HIGH |
| R2_telemetry_span | R2 Telemetry Span | ✅ OK | CRITICAL |
| R3_config_drift | R3 Config Drift | ✅ OK | HIGH |
| R4_missing_columns | R4 Missing Columns | ❌ NG | HIGH |
| R5_missing_files | R5 Missing Files | ✅ OK | CRITICAL |
| R6_friction_anomaly | R6 Friction Anomaly | ❌ NG | MEDIUM |
| R7_order_explosion | R7 Order Explosion | ❌ NG | HIGH |
| R8_latency_spikes | R8 Latency Spikes | ✅ OK | MEDIUM |
| R9_clock_drift | R9 Clock Drift | ✅ OK | HIGH |
| R10_gh_upload_fail | R10 Gh Upload Fail | ❌ NG | LOW |
| R11_risk_discipline | R11 Risk Discipline | ✅ OK | CRITICAL |
| R12_path_unicode | R12 Path Unicode | ✅ OK | MEDIUM |

---

## Report Files Generated

1. `kpi_overview.csv` - Comprehensive KPI summary
2. `missing_fields.csv` - Missing column analysis
3. `friction_stats.csv` - Fill rate and slippage statistics
4. `latency_stats.csv` - Latency percentiles and outliers
5. `gaps_detected.csv` - Telemetry time gaps
6. `packaging_check.csv` - Required files status
7. `config_check.json` - Configuration validation
8. `risk_gate_check.json` - Risk discipline violations
9. `audit_validator.json` - Complete risk assessment (R1-R12)
