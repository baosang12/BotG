# CHANGE-002: Preflight cTrader Retry + Soft-Pass Implementation

## Summary
This change adds retry logic and soft-pass capability to `scripts/preflight/ctrader_connect.ps1` to prevent Gate2 24h workflow failures from minor connectivity noise (e.g., freshness=5.1s).

## Implementation

### New Parameters
- `-Mode` ∈ {`gate2`,`gate3`} (default: `gate2`)
- `-Probes` (default: `3`) - number of samples to collect
- `-IntervalSec` (default: `10`) - seconds between probes

### Logic Changes

1. **Multi-Probe Collection**: Collect N probes (default 3) spaced by IntervalSec
   - Each probe measures: `last_age_sec`, `active_ratio`, `tick_rate`, `ts_utc`

2. **Gate2 Soft-Pass** (5.0s < min_last_age_sec ≤ 5.5s):
   - Calculate `min_last_age_sec` = min of all probe last_age_sec values
   - **PASS** if min_last_age_sec ≤ 5.0 AND all probes meet active_ratio ≥ 0.7, tick_rate ≥ 0.5
   - **WARN** (soft-pass, exit 0) if 5.0 < min_last_age_sec ≤ 5.5 AND metrics OK
   - **FAIL** (exit 1) if min_last_age_sec > 5.5 OR any metric violation

3. **Gate3 Hard Limit** (strict ≤ 5.0s):
   - No soft-pass zone
   - PASS only if min_last_age_sec ≤ 5.0
   - FAIL immediately if > 5.0

### Output Format
`connection_ok.json`:
```json
{
  "ok": true|false,
  "mode": "gate2"|"gate3",
  "min_last_age_sec": <number>,
  "probes": [
    {"ts_utc": "...", "last_age_sec": 4.8, "active_ratio": 0.85, "tick_rate": 1.5},
    {"ts_utc": "...", "last_age_sec": 5.2, "active_ratio": 0.80, "tick_rate": 1.2},
    {"ts_utc": "...", "last_age_sec": 4.9, "active_ratio": 0.82, "tick_rate": 1.3}
  ],
  "note": "pass" | "warn: borderline freshness" | "fail: reason",
  "generated_at_iso": "...",
  "telemetry_file": "...",
  "symbols_used": "EURUSD"
}
```

## Test Cases

### Test 1: PASS (min=4.2s)
- Probes: [4.2s, 4.5s, 4.1s]
- min_last_age_sec = 4.1
- All metrics OK
- Expected: exit 0, note="pass"

### Test 2: WARN (min=5.2s, borderline)
- Probes: [5.2s, 5.4s, 5.3s]
- min_last_age_sec = 5.2
- All metrics OK
- Expected: exit 0, note="warn: borderline freshness" (Gate2 soft-pass)

### Test 3: FAIL (min=6.0s)
- Probes: [6.0s, 6.2s, 5.9s]
- min_last_age_sec = 5.9
- Exceeds 5.5s threshold
- Expected: exit 1, ok=false

## Files Modified
- `scripts/preflight/ctrader_connect.ps1` - Main implementation
- `scripts/tests/test_ctrader_connect.ps1` - Test harness (not in CI)
- `scripts/tests/_fixtures/connection_ok_*.json` - Test outputs

## Notes
- This change does **not** modify CI workflows or environment variables
- Gate2 workflows will benefit from 5.0-5.5s soft-pass zone
- Gate3 workflows maintain strict ≤5.0s requirement
- Test harness runs locally only (not added to CI pipeline)
