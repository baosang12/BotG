[![Smoke (PR)](https://github.com/baosang12/BotG/actions/workflows/smoke-on-pr.yml/badge.svg)](https://github.com/baosang12/BotG/actions/workflows/smoke-on-pr.yml)

[![CI - smoke-selftest](https://github.com/baosang12/BotG/actions/workflows/smoke-selftest.yml/badge.svg)](https://github.com/baosang12/BotG/actions/workflows/smoke-selftest.yml)

[![CI - build & test](https://github.com/baosang12/BotG/actions/workflows/smoke-selftest.yml/badge.svg)](https://github.com/baosang12/BotG/actions/workflows/smoke-selftest.yml)

# BotG Runtime Configuration

- Risk.PointValuePerUnit: Monetary value (in account currency) per one price unit per one volume unit. Used for sizing when converting price stop distance to risk per unit.
- Default is 1.0. TODO: replace with the broker-specific contract value (e.g., XAUUSD on ICMarkets RAW).
- Configure in `config.runtime.json` at repo root:

```
{
  "Risk": {
    "PointValuePerUnit": 1.0,
    "RiskPercentPerTrade": 0.01,
    "MinRiskUsdPerTrade": 3.0,
    "StopLossAtrMultiplier": 1.8
  }
}
```

Notes:
- If the config is missing, defaults are used and the app continues to run.
- ATR-based SL is still TODO to be wired with a real provider.

## Realtime run: safe sequence

Windows (PowerShell 5.1) recommended steps:

1) Quick smoke (60s) to validate pipeline
  - VS Code task: `realtime-quick-60s`
  - Or run: `scripts/start_realtime_1h_ascii.ps1 -Seconds 60 -SecondsPerHour 60`

2) Preflight health check
  - VS Code task: `preflight-last-ascii`
  - Or run: `scripts/health_check_preflight.ps1 -ProcId <pid> -OutDir <outdir>`

3) Start 1-hour realtime
  - VS Code task: `realtime-1h-ascii`
  - Or run: `scripts/start_realtime_1h_ascii.ps1 -Seconds 3600 -SecondsPerHour 3600`

4) Monitor logs and finalize (if daemon didnâ€™t finalize)
  - VS Code task: `finalize-last-ascii`
  - Or run: `scripts/finalize_realtime_1h_report.ps1 -OutBase <outdir>`

5) Acceptance gate
  - In `reconstruct_report.json`, require `estimated_orphan_fills_after_reconstruct == 0`.
  - `final_report.json` contains a summarized verdict. The daemon retries once automatically if orphans persist.

## Development

### Path hardening (Windows, OneDrive)

If your OneDrive folder contains Unicode in its name (for example, `D:\OneDrive\TÃ i Liá»‡u\...`), prefer an ASCII-safe root and set an environment variable for the repo:

- Recommended repo path: `D:\OneDrive\TaiLieu\cAlgo\Sources\Robots\BotG`
- Set once per session:
  - PowerShell: `./scripts/set_repo_env.ps1 -BotGRoot "D:\OneDrive\TaiLieu\cAlgo\Sources\Robots\BotG"`
  - Or set permanently as user env `BOTG_ROOT`.

Scripts now reference `$env:BOTG_ROOT` (and `$env:BOTG_RUNS_ROOT`/`$env:BOTG_LOG_PATH` when applicable) instead of hard-coded absolute paths.
## Gate2 24h paper run

See docs/RUNBOOK_gate2.md for PASS criteria, how to run the one-shot workflow, and validator details.

<!-- Test no-op guard: 2025-10-14 20:38:50 -->
