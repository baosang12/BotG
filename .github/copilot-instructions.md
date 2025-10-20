# BotG Copilot Quickstart (≤50 lines)

**Goal**: Onboard AI agents to code safely & be productive immediately.

**Architecture (what→why)**:
- Entry `BotG/BotGRobot.cs` → `BotGStartup.Initialize()` ONCE (wires Telemetry, Risk, Connectivity, TradeManager/Execution, Strategies).
- Mode switch via `Connectivity/ConnectorBundle` (`DATASOURCE__MODE`: `ctrader_demo` | `synthetic`).
- Flow: Strategy → TradeManager (RiskEvaluator + daily limits) → ExecutionModule (orders) → Telemetry.
- Data: `DataFetcherService` caches ticks/bars; pushes account state → risk snapshots.

**Risk/Execution**:
- Always init `RiskManager` before `ExecutionModule`; sizing = `RiskManager.CalculateOrderSize` (no fallback).
- No martingale/grid; respect LV0 R=$10; daily −3R; weekly −6R.

**Telemetry & Artifacts**:
- Call `TelemetryContext.InitOnce()` exactly once → writers for `orders.csv`, `risk_snapshots.csv`, `telemetry.csv`, `run_metadata.json`.
- Layout `<BOTG_LOG_PATH>/artifacts/telemetry_run_*`; Gate2: span ≥ 23h45m; orders has REQUEST/ACK/FILL + status/reason/latency/price_*; snapshots ≥ ~1300 rows; reconstruct orphan_fills = 0.

**Dev commands (Windows/PS 5.1)**:
- Quick hour: `./scripts/run_paper_pulse.ps1 -Hours 1 -SecondsPerHour 300`
- Smokes: `./scripts/run_smoke.ps1`, `./scripts/run_smoke_60m_wrapper_v2.ps1`
- Tests: `dotnet test Tests/BotG.Tests.csproj`
- Analyzer: `pip install -r scripts/requirements.txt` then `python scripts/postrun_report.py --orders ... --risk ... --out ...`

**Guardrails**:
- Do NOT change telemetry schema or duplicate InitOnce.
- Do NOT bypass TradeManager or day limits.
- Keep env vars: `BOTG_LOG_PATH`, `DATASOURCE__MODE`, `BROKER__*`, `L1_SNAPSHOT_HZ`, `BOTG_RISK_FLUSH_SEC`.

(Full details below.)

# BotG Copilot Instructions (v2)
**Scope & audience.** This file onboards AI coding agents (Copilot/Claude/Cursor/GPT) to BotG so they can contribute *immediately* and safely. Document real, discoverable patterns only.

## Big picture — Architecture (**what & why**)
- **Runtime entrypoint:** `BotG/BotGRobot.cs` → always call `BotGStartup.Initialize()` *once* to wire **Telemetry**, **Risk**, **Connectivity**, **TradeManager/Execution**, **Strategies**. *Why:* guarantees writers/guards are ready before any trading logic.
- **Composition hub:** `Connectivity/ConnectorBundle.cs` selects **cTrader** vs **Synthetic** by `DATASOURCE__MODE`. *Why:* switchable I/O for local tests vs paper/live without forking code.
- **Signal → Risk → Execution path:** Strategies emit `Strategies/TradeSignal.cs` → `TradeManager` screens via `RiskEvaluator/RiskEvaluator.cs` and day limits → `Execution/ExecutionModule.cs` places orders. *Why:* single choke-point for risk discipline.
- **Data ingestion:** `DataFetcher/Service/DataFetcherService.cs` caches ticks/bars & pushes account info into telemetry. *Why:* keeps `risk_snapshots.csv` consistent even when market-data is bursty.
- **Stable contracts:** Connectivity types in `Connectivity/Contracts.cs` are reused by feeds/executors. *Why:* keeps telemetry schema & analyzers stable across providers.

## Connectivity
- **cTrader stack:** `Connectivity/CTrader/CTraderConnector.cs` + executor. Use `Subscribe` → `Start`; pump via `ConnectorBundle.TickPump` inside `OnTick`. Throttle L1 ~6 Hz (avoid blocking robot thread).
- **Synthetic stack:** `Connectivity/Synthetic/SyntheticProvider.cs` for deterministic fills/slippage. Tests set `DATASOURCE__MODE=synthetic`.
- **Adding a provider:** implement `IMarketDataProvider` & `IOrderExecutor`, raise events, and call `TelemetryContext.AttachConnectivity(...)` so run metadata is populated.
- **Env toggles:** `DATASOURCE__MODE` (defaults `ctrader_demo`), `BROKER__NAME`, `BROKER__SERVER`, `BROKER__ACCOUNT_ID` — all end up in run metadata.

## Risk & Execution
- **RiskManager:** `RiskManager/RiskManager.cs` loads `config.runtime.json`; override with env (e.g., `BOTG_RISK_FLUSH_SEC`). Initialize **before** `ExecutionModule`/`TradeManager`. *No fallback sizing.*
- **Sizing:** `RiskManager.CalculateOrderSize(stop, symbol)` converts stop distance & point value (auto-computed with `TryAutoComputePointValueFromSymbol`). Unit tests enforce monotonicity.
- **Trade gating:** `TradeManager.CanTrade` enforces per‑day limits & minimum risk score; coordinates with `RiskEvaluator` (Wyckoff/SMC heuristics). **No martingale/grid.**
- **Execution:** `ExecutionModule` logs `REQUEST/ACK/FILL` via `Telemetry/OrderLifecycleLogger.cs`; inject the **same** `RiskManager` instance used for sizing.

## Telemetry & Artifacts (Gate‑critical)
- **Init:** `TelemetryContext.InitOnce()` (exactly once) creates the run folder and writers for `orders.csv`, `risk_snapshots.csv`, `telemetry.csv`, plus `run_metadata.json` via `RunMetadataWriter`.
- **L1 snapshots:** `TelemetryContext.AttachLevel1Snapshots` samples quotes at `L1_SNAPSHOT_HZ` (default 5). Throttle at provider if you add high‑freq symbols.
- **Closed trades / FIFO:** `Telemetry/ClosedTradesWriter` maintains ID continuity; the postrun FIFO reconstruction & tests depend on it.
- **Artifacts layout:** `<BOTG_LOG_PATH>/artifacts/telemetry_run_*` (Windows default falls back to LocalAppData). Ops & CI assume this layout.
- **Minimums for Gate2 (24h paper):** span ≥ 23h45m; `orders.csv` has REQUEST/ACK/FILL with `status, reason, latency, price_requested, price_filled`; `risk_snapshots.csv` ≥ ~1300 rows/24h; `run_metadata.json` mode=`paper`.
- **Reconstruct rule:** postrun JSON must report `estimated_orphan_fills_after_reconstruct == 0`.

## CI/CD Gates & risk discipline (ops facts the AI must respect)
- **Gates:** Gate2 (24h paper) → Gate3 (5×24h, ≥3 phiên dương) → Gate4 (live 0.01 lot) → Gate5 (scale).
- **Risk limits:** LV0 `R=$10`; daily stop −3R; weekly stop −6R; sizing always via `RiskManager.CalculateOrderSize`.
- **No‑go changes for AI:** do not alter telemetry schemas, do not duplicate/disable `InitOnce`, do not bypass trade gating, do not add martingale/grid.

## Dev workflows (Windows/PowerShell 5.1 first)
- **Quick compressed hour:** `./scripts/run_paper_pulse.ps1 -Hours 1 -SecondsPerHour 300` → outputs under `artifacts_ascii/telemetry_run_*`.
- **Smokes & preflight:** `./scripts/run_smoke.ps1`, `./scripts/health_check_preflight.ps1`, `./scripts/run_smoke_60m_wrapper_v2.ps1` (respects `CI_BLOCK_FAIL`).
- **Realtime demo (paper):** `./scripts/start_realtime_1h_ascii.ps1`.
- **.NET tests:** `dotnet test Tests/BotG.Tests.csproj` (xUnit). Clean env overrides after running (notably `DATASOURCE__MODE`).
- **Python analyzers:** `pip install -r scripts/requirements.txt`; then `python scripts/postrun_report.py --orders <path> --risk <path> --out <dir>`.
- **Precommit sanity:** `./scripts/precommit_check.ps1` (PS/Python syntax, optional Pester).

## Configuration conventions
- **Discovery:** `TelemetryConfig.FindConfigPath` walks up to 5 parents to find `config.runtime.json` — keep overrides near scripts; never hard‑code absolute paths.
- **Env vars to respect:** `BOTG_LOG_PATH`, `BOTG_ROOT`, `BOTG_RUNS_ROOT`, `DATASOURCE__MODE`, `BROKER__*`, `BOTG_RISK_FLUSH_SEC`, `L1_SNAPSHOT_HZ`, and `SecondsPerHour` in harness scripts.
- **Path safety:** scripts assume ASCII‑safe paths; guard against OneDrive Unicode paths (see `scripts/set_repo_env.ps1`).

## Strategy pattern (how to add a new strategy safely)
- Implement a class that outputs `TradeSignal` with required fields (symbol, side, entry/SL/TP or stop distance). Keep it **stateless** or manage state via explicit context; avoid hidden singletons.
- Register through `TradeManager` (do not call `ExecutionModule` directly). Risk screening must run before any order hits the executor.

## Postrun & reports
- After a run, collect artifacts and run reconstruct + KPI report: `scripts/postrun_report.py` → JSON/PDF. Expect zero orphan fills; review slippage p50/p95, fill‑rate, MAE/MFE, latency, −3R hits.
- Promotion to Gate3 requires ≥3 phiên dương trong 5×24h và không vi phạm risk stops.

## Guardrails & anti‑patterns
- ❌ Creating multiple `TelemetryContext`/writers; ❌ changing column names; ❌ skipping `RunMetadataWriter`; ❌ blocking in robot callbacks; ❌ leaking `DATASOURCE__MODE` into static caches; ❌ bypassing `TradeManager`.
- ✅ Prefer queues/timers for heavy work; ✅ share singletons via DI/bootstrap; ✅ keep analyzers & schemas in lock‑step (update tests when adding counters).

## Quick checklist for AI edits
1) Will this change alter telemetry schema or run layout? If yes, stop & update analyzers/tests/docs.  
2) Are `RiskManager` and `TelemetryContext` initialized exactly once?  
3) Does the code path still flow: **Strategy → TradeManager (risk) → ExecutionModule**?  
4) Do scripts still honor env vars & Windows PS 5.1?  
5) Have you run `dotnet test` + a smoke (`run_smoke_60m_wrapper_v2.ps1`) locally?
