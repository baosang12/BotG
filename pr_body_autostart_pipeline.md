**AutoStart Executor & TradeManager Pipeline + Single-Switch ops.enable_trading + SmokeOnce One-Cycle Broker Test**

## Summary
Implements **Runtime AutoStart** pattern: bot pipeline runs immediately upon cTrader attach, controlled by **single-switch `ops.enable_trading`** gate. Adds **comprehensive pipeline logging** (JSON events to console + `pipeline.log`). Implements **`debug.smoke_once`** one-cycle broker test (REQUEST→ACK→FILL→CLOSE). Writes **`runtime_probe.json`** every 1s with runtime state. Enhances **`executor_wireproof.json`** with `ops_enable_trading` field.

## Changes
### TelemetryConfig.cs
- **OpsConfig class**: `enable_trading` (bool, default=true) - single master trading switch
- **DebugConfig class**: `smoke_once` (bool, default=false) - one-cycle broker verification
- **Load()**: PropertyNameCaseInsensitive=true for kebab-case/camelCase config support
- **Merged**: Ops & Debug sections from config files

### PipelineLogger.cs (NEW)
- **JSON logging utility**: writes to both Console ([PIPE][MODULE] prefix) and `D:\botg\logs\pipeline.log`
- **Format**: `{ts, lvl, mod, evt, msg, data}` - structured events for analysis
- **Thread-safe**, async writes to avoid blocking runtime

### BotGRobot.cs - OnStart()
- **[PIPE] logging**: Comprehensive boot events (BOOT, EXECUTOR, WRITER, RISK, TRADE modules)
- **Executor ready**: Set `_executorReady=true` when connector bundle initialized
- **Wireproof enhanced**: Added `ops_enable_trading` field, `ok=(trading_enabled && executorReady && opsEnableTrading)`
- **Preflight non-blocking**: Failures logged but DON'T stop bot (AutoStart pattern)
- **RuntimeLoop kick**: Called immediately after OnStart (not waiting for first timer tick)
- **ECHO+**: Logs `ExecutorReady={bool}, enable_trading={bool}, smoke_once={bool}` at boot completion

### BotGRobot.cs - OnTimer() & RuntimeLoop()
- **OnTimer**: Calls `RuntimeLoop()` every 1s (not tick-dependent)
- **RuntimeLoop()**:
  - Always calls `TradeManager.Process()` (NOT blocked by mode/simulation)
  - Updates `D:\botg\logs\runtime_probe.json` every cycle with runtime state
  - **SmokeOnce logic**: if `cfg.Debug.SmokeOnce && !_smokeOnceDone && _executorReady`:
    - Calculate lots via RiskManager (100-point stop, fallback to minLot)
    - Execute Market BUY → log REQUEST/ACK/FILL
    - Close position immediately → log CLOSE
    - Set `_smokeOnceDone=true` to prevent retries
  - **ORDER pipeline logging**: [PIPE][ORDER] REQUEST/ACK/FILL/CLOSE with latency_ms
  - **SMOKE logging**: [PIPE][SMOKE] Start/Complete events

### TradeManager.cs - CanTrade()
- **SINGLE-SWITCH GATE**: `if (!cfg.Ops.EnableTrading) return false`
- **Pipeline logging**: [PIPE][TRADE] CanTrade=false (ops gate) when blocked
- **TODO added**: Hard-stop risk gates (-3R daily, -6R weekly) deferred to future PR

## Evidence (grep verified)
✅ **RuntimeLoop()** exists and called from OnTimer  
✅ **PipelineLogger** used throughout OnStart (BOOT, EXECUTOR, WRITER, RISK, TRADE events)  
✅ **cfg.Ops.EnableTrading** gate in TradeManager.CanTrade  
✅ **SmokeOnce logic** with `_smokeOnceDone` guard  
✅ **runtime_probe.json** written every RuntimeLoop cycle  
✅ **ops_enable_trading** field in wireproof  
✅ **OpsConfig & DebugConfig** classes added  
✅ **Case-insensitive JSON binding**  

## Build
✅ `dotnet build BotG.sln -c Release` **succeeded** (176 warnings, 0 errors)

## Testing Scenarios
### 1. AutoStart Verification
- Attach bot to cTrader → verify immediate [PIPE][BOOT] Complete log
- Check `executor_wireproof.json` has `ops_enable_trading: true` and `ok: true`
- Verify `runtime_probe.json` updates every 1s

### 2. SmokeOnce One-Cycle Test
- Set config with `debug.smoke_once: true` and `ops.enable_trading: true`
- Attach bot → verify logs show REQUEST/ACK/FILL/CLOSE cycle
- Verify `runtime_probe.json` has `smokeOnceDone: true`
- Verify position opened and closed in broker history

### 3. ops.enable_trading Gate
- Set config with `ops.enable_trading: false`
- Attach bot → verify [PIPE][TRADE] CanTrade=false (ops gate) in logs
- Verify NO orders sent to broker
- Set `enable_trading: true` → verify trading resumes

### 4. Pipeline Logging
- Check `D:\botg\logs\pipeline.log` exists
- Verify JSON format with ts, lvl, mod, evt, msg, data fields
- Verify Console has [PIPE][MODULE] formatted logs

## No Workflow Changes
- **OPS workflows**: Unchanged, ready for CI validation
- **gate24h.yml**: Can add check for `ops_enable_trading` field in wireproof (future enhancement)
- **No martingale/grid**: Not implemented (as required)

## Risk Management
- **Hard-stops TODO**: -3R daily, -6R weekly limits added as TODO in TradeManager.CanTrade
- **SmokeOnce**: Uses RiskManager.CalculateOrderSize for proper volume calculation
- **Single-switch control**: ops.enable_trading provides master kill switch for operator

## Notes
- **Strategy pipeline empty**: Intentional (documented in code), ready for future strategies
- **Mode/simulation blocking removed**: Runtime now starts regardless of mode/sim config
- **Preflight async**: Runs in background, does NOT block AutoStart
- **Case-insensitive config**: Supports both `enable_trading` and `EnableTrading` in JSON
