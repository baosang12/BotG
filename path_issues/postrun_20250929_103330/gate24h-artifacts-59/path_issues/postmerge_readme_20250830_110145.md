# Weekend Full Readiness - 20250830_110145

## Gates
| Gate | Status |
|------|--------|
| Build | PASS |
| Smoke | FAIL |
| Reconstruct | MISSING |
| Fill rate | N/A |
| Logging | FAIL |

## Artifacts (checksummed)
| Name | File | Size | mtime (UTC) | SHA256 |
|------|------|------|-------------|--------|
| fillrate_by_hour.png | fillrate_by_hour.png | 5316 | 2025-08-27T07:04:24.3343173Z | A38B26D2E99EE641B7A1D8F2F2DFD89519BDEE0F038BE6DEE9F71F7026C39A57 |
| fillrate_hourly.csv | fillrate_hourly.csv | 103 | 2025-08-27T06:48:40.6523286Z | 0BA2358871E1B28D95FE4435EC82ACE8BA42C79B762B20C6ED89943D99FDCA11 |
| latency_percentiles.png | latency_percentiles.png | 5360 | 2025-08-27T07:04:24.3300403Z | B9BBD55294881EF9D522397ADB9E6AF829B016D575B0F39D77B06C09042CD4DA |
| orders_ascii.csv | orders_ascii.csv | 10927 | 2025-08-25T06:19:11.8005244Z | D68FEA28C52BD0DBE25A048959EE58C8EFE2818EEE1F6FCAB3E48C6EBD9D9EC1 |
| slippage_hist.png | slippage_hist.png | 5176 | 2025-08-27T07:04:24.3097841Z | 649ECC508286137060C2260915394948CFB7B7CBC2E543CE25AB1D8ABAC8629F |
| telemetry_run_20250827_200336.zip | telemetry_run_20250827_200336.zip | 48901 | 2025-08-27T13:05:39.9629432Z | 3F88D20B0AF48EC6A2FBBFC4C13F5D8FE238E608868E538476BEB52BF29DA6D1 |
| top_slippage.csv | top_slippage.csv | 116 | 2025-08-27T06:48:40.6523286Z | D02A1184FB37B6301F3BC7EC76F1F307770E47B624C7CFD2832E2A8DB2C79550 |

## Copilot Readiness Statement
Some gates did not pass. See blockers and logs.

## Caveats (not a 100% mathematical guarantee)
- External broker/live environment not tested
- Production-scale latency not measured
- Market halts/outages not simulated
- Extreme-volatility slippage untested

## Next action
Run 24h supervised on Monday at operator-chosen time.

## One-line
KET QUA: weekend full-check: FAIL | readiness: INVESTIGATE | Confidence: LOW | Caveats: External broker/live environment not tested; Production-scale latency not measured; Market halts/outages not simulated; Extreme-volatility slippage untested
