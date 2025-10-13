# Gate2 Risk Audit Report

**Audit Date**: 2025-10-11 14:15:04 UTC
**Artifacts Source**: C:\Users\TechCare\AppData\Local\Temp\botg_artifacts\gate2_18393525101\artifacts_20251010_081328

---

## What Changed (3 Key Findings)

### 1. R6 Friction Anomaly

**Status**: ❌ NG

**Evidence**:
- 100% fill rate: 100.00%
- Constant amplitude: p95-p5(|slip_abs|)=7.28e-12 < 1e-5
- Weak slip-latency correlation: corr=0.003 (< 0.15 threshold)

**Quantitative Impact**:
- Fill Rate (CORRECTED): 100.00% (REQUEST: 357,194, FILL: 357,194)

### 2. R7 Order Explosion

**Status**: ❌ NG

**Evidence**:
- Order explosion: 357,194 fills > 300k threshold

**Quantitative Impact**:
- Total Fills: 357,194

---

## So What (3 Impacts)

1. **Measurement Accuracy**: Previous fill_rate calculation (33.33%) was incorrect due to counting all status rows (REQUEST+ACK+FILL). Corrected calculation shows actual 100% fill rate, confirming paper mode behavior.

2. **Scale Issues**: 357K fills/24h exceeds threshold, indicating high-frequency strategy that may hit broker limits or cause packaging/storage issues.

---

## What Next (3 Immediate Actions + 1 A/B Change)

1. **Fix Validator Metrics**: Update `audit_gate2_risks.py` to calculate fill_rate as FILL/REQUEST (not FILL/total_rows). Add REQUEST/ACK/FILL counts to kpi_overview.csv for transparency.

2. **Add Order Rate Limiter**: Implement throttle in RiskManager to cap at 200 orders/min and 400K orders/day, preventing runaway loops.

---

## Single A/B Change for Next Gate2 Run

**🎯 RECOMMENDED A/B TEST**: Fix validator schema mapping and metrics

**Change**: Update `scripts/audit_gate2_risks.py` REQUIRED_COLUMNS:
```python
REQUIRED_COLUMNS = {
    'orders.csv': ['status', 'timestamp_request', 'timestamp_fill'],
    'risk_snapshots.csv': ['timestamp_utc', 'balance', 'equity', 'closed_pnl', 'open_pnl'],
    'telemetry.csv': ['timestamp_iso']
}
# Calculate fill_rate = FILL_count / REQUEST_count (not FILL/total_rows)
```

**Expected Impact**:
- R4 false positives eliminated (no missing columns)
- Fill rate corrected: 33.33% → 100.00% (REQUEST=357,194, FILL=357,194)
- Validator reports accurate, no wasted investigation time

**Validation**: Compare audit_validator.json before/after to confirm R4=OK and fill_rate=100%.

**IMPORTANT**: Gate2 = paper supervised, **NON-SIMULATION**. Do **NOT** add randomized slippage/rejection to Harness/PaperTradingEngine.cs. Near-zero slippage is expected behavior for paper mode.

---

## Summary Statistics

- **Total Requests**: 357,194
- **Total Fills**: 357,194
- **Fill Rate**: 100.00% (CORRECTED: FILL/REQUEST)
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
| R4_missing_columns | R4 Missing Columns | ✅ OK | HIGH |
| R5_missing_files | R5 Missing Files | ✅ OK | CRITICAL |
| R6_friction_anomaly | R6 Friction Anomaly | ❌ NG | MEDIUM |
| R7_order_explosion | R7 Order Explosion | ❌ NG | HIGH |
| R8_latency_spikes | R8 Latency Spikes | ✅ OK | MEDIUM |
| R9_clock_drift | R9 Clock Drift | ✅ OK | HIGH |
| R10_gh_upload_fail | R10 Gh Upload Fail | ✅ OK | LOW |
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
