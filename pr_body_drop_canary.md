## Objective

Drop Canary execution path and replace with lightweight ExecutorReady wireproof at startup.

## Changes

### Removed
- **Canary task execution** (lines 249-307): No longer spawns canary orders
- **`_canaryOnce` field**: Removed single-shot guard
- **3 unused helper methods**:
  - `WriteCanaryStatusJson`
  - `WriteCanaryWireProof` 
  - `WriteWireproofSnapshot`
- **3 wireproof snapshots** during connector init (init/executor_ready/armed)

### Added
- **Single executor_wireproof.json** at end of OnStart:
  ```json
  {
    "generated_at": "2025-10-31T...",
    "trading_enabled": bool,
    "connector": "CTraderConnector",
    "executor": "CTraderOrderExecutor",
    "ok": bool
  }
  ```
- **[ECHO+] ExecutorReady log** with executor type

### Documented
- **Strategy pipeline requirements** in OnTick:
  - Strategies list currently EMPTY (no orders placed)
  - To enable: add strategies + call `.Evaluate(data)` → signals → `TradeManager.Process()`

## Impact
- ✅ No strategy logic changes
- ✅ No orders will be placed (empty strategy list)
- ✅ Executor wiring verified via wireproof JSON
- ✅ Cleaner startup: -156 lines of code

## Verification
- ✅ Build successful (Release)
- ✅ File path: `D:\botg\logs\preflight\executor_wireproof.json`
- ✅ Expected log: `[ECHO+] ExecutorReady trading_enabled=<bool> executor=<type>`

## Next Steps
PR#2 will update gate24h.yml to remove canary step and require executor_wireproof.json check.
