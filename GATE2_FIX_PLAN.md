# Gate2 Fix Implementation Plan

## Status: IN PROGRESS

### Blockers to Fix:
1. ❌ RiskHeartbeat - 60s sampling guarantee
2. ❌ OrderLifecycleLogger - timestamp tracking  
3. ❌ run_metadata - correct runtime params
4. ❌ Commission/Spread - realistic costs

### Current Analysis:

**RiskSnapshotPersister.cs:**
- ✅ Has Persist() method writing to CSV
- ❌ Header missing: open_pnl, closed_pnl columns (has: timestamp,equity,balance,margin,free_margin,drawdown,R_used,exposure)
- ❌ No guaranteed 60s timer (depends on RiskManager calling it)
- ❌ No file rotation logic

**RiskManager.cs:**
- ✅ Has _snapshotTimer initialized
- ✅ Timer set to FlushIntervalSeconds in Initialize()
- ❌ Timer callback PersistSnapshotIfAvailable() only runs if _lastAccountInfo exists
- ❌ No guarantee of 60s if no account updates

**OrderLifecycleLogger.cs:**
- ✅ Has latency_ms tracking
- ✅ Has _requestEpochMs dictionary for REQUEST phase
- ❌ timestamp_request/ack/fill columns are EMPTY (placeholder at end of header)
- ❌ No actual ISO-8601 timestamp writing to those columns

**TelemetryConfig.cs:**
- ✅ Has Hours, SecondsPerHour, UseSimulation properties
- ❌ Default Hours=1 (should be 24 for production)
- ❌ Default UseSimulation=true (should be false for paper)

### Implementation Strategy:

1. **Fix RiskHeartbeat:**
   - Modify RiskSnapshotPersister to add missing columns
   - Ensure timer ALWAYS fires every 60s with last known AccountInfo
   - Add stub AccountInfo if none available (equity=10000, balance=10000, margin=0)

2. **Fix OrderLifecycleLogger:**
   - Add OrderLifecycleState class to track ts_request/ts_ack/ts_fill
   - Modify LogV2() to populate timestamp_request/ack/fill columns
   - Ensure latency_ms uses ts_fill - ts_request

3. **Fix run_metadata:**
   - Update RunInitializer.cs to read Hours from config/env
   - Default to hours=24, simulation.enabled=false, seconds_per_hour=3600 for paper mode

4. **Fix Commission/Spread:**
   - Add CommissionPerLotUsd to ExecutionConfig
   - Add SpreadMinPips to SimulationConfig
   - Update analyzer to compute P&L after fees

### Files to Modify:
- BotG/Telemetry/RiskSnapshotPersister.cs
- BotG/RiskManager/RiskManager.cs
- BotG/Telemetry/OrderLifecycleLogger.cs
- BotG/Telemetry/TelemetryConfig.cs
- BotG/Telemetry/RunInitializer.cs
- BotG/Execution/ExecutionModule.cs (if exists)

